using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Contracts.Core.Data;
using Contracts.Core.State;

namespace Contracts.Core.Sim
{
    /// <summary>
    /// The operations simulation: the waybill state machine (operations-design.md,
    /// section 8), industry ticks (9.2), ordering at interchange services (9.3)
    /// and the service plans the runtime executes (9.4). Engine-free: the
    /// runtime feeds <see cref="Step"/> with observations and executor results
    /// and applies the commands it returns.
    /// </summary>
    public sealed partial class OpsEngine
    {
        /// <summary>A deferred service is retried at most once per game minute.</summary>
        public const double RetryMinutes = 1.0;

        /// <summary>No component orders more than this many cars at one service.</summary>
        public const int MaxOrderPerComponent = 10;

        /// <summary>Space left between spawned cars and around cars already on a spot, m.</summary>
        public const double CarGap = 0.3;

        private const double Epsilon = 1e-9;

        private readonly SplitMix64 rng;

        // Session-only knowledge: rebuilt from observations after every load.
        private readonly Dictionary<string, CarObservation> seen = new Dictionary<string, CarObservation>(StringComparer.Ordinal);
        private readonly HashSet<string> seenThisStep = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> dwellSince = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly HashSet<string> rerouteOnSight = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ServiceCut>> attemptCuts = new Dictionary<string, List<ServiceCut>>(StringComparer.Ordinal);
        private StepResult result = new StepResult();

        public OpsEngine(OpsData data, OpsState state)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            State = state ?? throw new ArgumentNullException(nameof(state));
            rng = new SplitMix64(state.Rng);
        }

        public OpsData Data { get; }

        public OpsState State { get; }

        public double Now => State.Clock.OpsMinutes;

        private OpsSettings Settings => Data.Settings;

        /// <summary>A fresh state: storage half full, the first service due at once or at the service hour.</summary>
        public static OpsState NewState(OpsData data, double worldMinutes, ulong seed)
        {
            var state = new OpsState
            {
                Map = data.Map,
                OpsDataSha256 = data.Sha256,
                Clock = OpsClock.Start(worldMinutes),
                Rng = seed,
            };
            foreach (var industry in data.Industries)
                state.Industries[industry.Id] = NewIndustryState(industry, data.Settings);
            var first = data.Settings.ServeOnNewSave ? 0.0 : OpsClock.NextDailyAfter(state.Clock, 0.0, data.Settings.ServeHour);
            foreach (var x in data.Interchanges)
                state.Interchanges[x.Id] = new InterchangeState { NextServiceAt = first };
            state.NextMidnightAt = OpsClock.NextDailyAfter(state.Clock, 0.0, 0);
            return state;
        }

        private static IndustryState NewIndustryState(Industry industry, OpsSettings settings)
        {
            var s = new IndustryState { Level = StartLevel(industry, settings) };
            var factor = Factor(industry, s.Level, settings);
            foreach (var cap in industry.Storage)
                s.Storage[cap.Key] = 0.5 * cap.Value * factor;
            return s;
        }

        // ------------------------------------------------------------ queries

        public CarObservation Observation(string guid) => guid != null && seen.TryGetValue(guid, out var o) ? o : null;

        public double Storage(string industryId, string loadId) =>
            State.Industries.TryGetValue(industryId, out var s) && s.Storage.TryGetValue(loadId, out var v) ? v : 0.0;

        /// <summary>The storage cap in carloads, scaled by the industry's contract.</summary>
        public double StorageCap(string industryId, string loadId)
        {
            var industry = Data.Industries.FirstOrDefault(i => i.Id == industryId);
            return industry != null && industry.Storage.TryGetValue(loadId, out var v) ? v * ContractFactor(industry) : 0.0;
        }

        /// <summary>Whether the car stands on one of its waybill destination's spots, stationary and of an accepted class.</summary>
        public bool IsAtDestination(Waybill waybill)
        {
            if (waybill == null || !State.Cars.TryGetValue(waybill.CarGuid, out var car))
                return false;
            var dest = Data.Destination(waybill.Dest);
            return dest != null && Accepts(dest, car) && IsStationaryOn(Observation(car.Guid), dest.Spots);
        }

        public bool IsStationaryOn(CarObservation o, IReadOnlyList<Spot> spots) =>
            o != null && o.Present && !o.Derailed && o.Speed <= Settings.SpotSpeed && IsOn(o, spots);

        public static bool IsOn(CarObservation o, IReadOnlyList<Spot> spots)
        {
            if (o == null || !o.Present)
                return false;
            foreach (var spot in spots)
            {
                if ((o.Front != null && spot.Contains(o.Front.Track, o.Front.Span)) || (o.Rear != null && spot.Contains(o.Rear.Track, o.Rear.Span)))
                    return true;
            }
            return false;
        }

        private static bool Accepts(IDestination dest, CarRecord car) => !(dest is IndustryComponent c) || c.Accepts(car.ClassId);

        // ------------------------------------------------------------ resume

        /// <summary>
        /// Reconciles a loaded state with the current static data
        /// (operations-design.md, 14.2) and restates every car's plate, which
        /// Derail Valley does not save. Call once after loading, before the
        /// first <see cref="Step"/>.
        /// </summary>
        public StepResult Resume()
        {
            result = new StepResult();
            if (State.OpsDataSha256 != Data.Sha256)
                Info("the operations data changed since this state was saved; reconciling");
            State.Map = Data.Map;
            State.OpsDataSha256 = Data.Sha256;

            foreach (var industry in Data.Industries)
            {
                if (!State.Industries.TryGetValue(industry.Id, out var s))
                {
                    State.Industries[industry.Id] = NewIndustryState(industry, Settings);
                    continue;
                }
                foreach (var cap in industry.Storage)
                    s.Storage[cap.Key] = s.Storage.TryGetValue(cap.Key, out var v) ? Math.Max(0.0, Math.Min(cap.Value, v)) : 0.5 * cap.Value;
                foreach (var gone in s.Storage.Keys.Where(k => !industry.Storage.ContainsKey(k)).ToList())
                    s.Storage.Remove(gone);
            }
            foreach (var gone in State.Industries.Keys.Where(k => Data.Industries.All(i => i.Id != k)).ToList())
                State.Industries.Remove(gone);

            foreach (var x in Data.Interchanges)
            {
                if (!State.Interchanges.ContainsKey(x.Id))
                    State.Interchanges[x.Id] = new InterchangeState { NextServiceAt = OpsClock.NextDailyAfter(State.Clock, Now, Settings.ServeHour) };
            }
            foreach (var gone in State.Interchanges.Keys.Where(k => Data.Interchange(k) == null).ToList())
            {
                Warn($"interchange '{gone}' is gone from the data; dropping its service state");
                State.Interchanges.Remove(gone);
            }
            foreach (var st in State.Interchanges.Values.Where(s => s.Pending != null))
                st.Pending.Cars.RemoveAll(c => Data.Destination(c.Dest) == null || !Data.CarClasses.ContainsKey(c.ClassId));
            // A state migrated from schema 1 knows neither its contract levels nor its next midnight.
            foreach (var industry in Data.Industries.Where(i => State.Industries[i.Id].Level == StateSerializer.Unset))
                State.Industries[industry.Id].Level = StartLevel(industry, Settings);
            if (State.NextMidnightAt < 0)
                State.NextMidnightAt = OpsClock.NextDailyAfter(State.Clock, Now, 0);

            foreach (var car in State.Cars.Values.ToList())
            {
                if (Data.Interchange(car.Home) == null && Data.Interchanges.Count > 0)
                    car.Home = Data.Interchanges[0].Id;
                if (car.WaybillId != null && !State.Waybills.ContainsKey(car.WaybillId))
                    car.WaybillId = null;
            }
            foreach (var wb in State.Waybills.Values.ToList())
            {
                if (!State.Cars.TryGetValue(wb.CarGuid, out var car) || car.WaybillId != wb.Id)
                {
                    Warn($"waybill {wb.Id} has no car; dropping it");
                    State.Waybills.Remove(wb.Id);
                    continue;
                }
                if (Data.Destination(wb.Dest) == null || Data.Destination(wb.Origin) == null)
                {
                    Warn($"waybill {wb.Id} names a destination or origin that is gone ({wb.Origin} → {wb.Dest}); rerouting {car.Mark} home");
                    var observedLoad = car.LoadId;
                    State.Waybills.Remove(wb.Id);
                    car.WaybillId = null;
                    car.Progress = 0.0;
                    RouteHome(car, observedLoad, Data.Destination(wb.Origin)?.Id ?? car.Home, paid: true);
                }
            }
            foreach (var car in State.Cars.Values.Where(c => c.WaybillId == null))
                RouteHome(car, car.LoadId, car.Home, paid: true);

            foreach (var car in State.Cars.Values)
                Plate(car);
            foreach (var problem in Invariants.Check(Data, State))
                Warn("invariant: " + problem);
            return result;
        }

        // ------------------------------------------------------------ step

        public StepResult Step(StepInput input)
        {
            result = new StepResult();
            seenThisStep.Clear();
            foreach (var e in input.Events)
                Handle(e);
            Observe(input);

            var advance = OpsClock.Observe(State.Clock, input.WorldMinutes, Settings.MaxCatchUpDays);
            result.Clock = advance;
            if (advance.Rejected != null && advance.Rejected.Length > 0)
                Warn("clock: " + advance.Rejected);

            DetectSpotting(input.SessionSeconds);
            foreach (var (t0, dt) in OpsClock.Steps(advance.Start, advance.Minutes))
                Tick(dt);
            RunMidnights();
            RunServices();
            State.Rng = rng.State;
            return result;
        }

        /// <summary>Makes an interchange's next service due now (console "serve", or a trainmaster's request).</summary>
        public bool RequestService(string interchangeId)
        {
            if (!State.Interchanges.TryGetValue(interchangeId ?? "", out var st))
                return false;
            if (st.Pending == null)
                st.NextServiceAt = Math.Min(st.NextServiceAt, Now);
            return true;
        }

        private void Observe(StepInput input)
        {
            foreach (var o in input.Cars)
            {
                if (o?.Guid == null || !State.Cars.TryGetValue(o.Guid, out var car))
                    continue;
                if (!o.Present)
                {
                    LoseCar(car, "it no longer exists");
                    continue;
                }
                seen[o.Guid] = o;
                seenThisStep.Add(o.Guid);
                var bogie = o.Front?.Track != null ? o.Front : o.Rear;
                if (bogie?.Track != null)
                    car.LastSeen = new CarSighting { Track = bogie.Track, Span = Math.Round(bogie.Span, 2), T = Now };
                if (rerouteOnSight.Remove(o.Guid) && car.WaybillId == null)
                    RouteHome(car, LoadForCargo(o.Cargo), car.Home, paid: false);
            }
        }

        private void Handle(ExecutorEvent e)
        {
            switch (e)
            {
                case CarDeleted deleted:
                    if (deleted.Guid != null && State.Cars.TryGetValue(deleted.Guid, out var car))
                        LoseCar(car, "Derail Valley deleted it");
                    break;
                case CargoFailed failed:
                    if (failed.Guid != null && State.Cars.TryGetValue(failed.Guid, out var c))
                    {
                        Warn($"cargo change on {c.Mark} failed: {failed.Reason}");
                        if (c.WaybillId != null && State.Waybills.TryGetValue(c.WaybillId, out var wb))
                            Void(wb, "its cargo could not be changed", writeOff: false);
                        rerouteOnSight.Add(c.Guid);
                    }
                    break;
                case ServiceResult service:
                    HandleService(service);
                    break;
            }
        }

        // ------------------------------------------------------------ waybill machine

        private void DetectSpotting(double sessionSeconds)
        {
            foreach (var wb in State.Waybills.Values.Where(w => w.State == WaybillState.Open).ToList())
            {
                if (!seenThisStep.Contains(wb.CarGuid))
                    continue;
                if (!IsAtDestination(wb))
                {
                    dwellSince.Remove(wb.CarGuid);
                    continue;
                }
                if (!dwellSince.TryGetValue(wb.CarGuid, out var since) || since > sessionSeconds)
                {
                    dwellSince[wb.CarGuid] = sessionSeconds;
                    since = sessionSeconds;
                }
                if (sessionSeconds - since + Epsilon >= Settings.SpotDwellSeconds)
                    Spot(wb);
            }
        }

        /// <summary>Open → Servicing (industry) or AwaitingPickup (interchange), paying exactly once.</summary>
        private void Spot(Waybill wb)
        {
            var car = State.Cars[wb.CarGuid];
            var dest = Data.Destination(wb.Dest);
            dwellSince.Remove(wb.CarGuid);
            if (wb.PaidAt != null)
            {
                Error($"waybill {wb.Id} was already paid; not paying again");
            }
            else
            {
                wb.PaidAt = Now;
                Received(wb);
                var total = wb.Pay + wb.Bonus;
                var extras = (wb.Bonus > 0 ? $" incl. ${wb.Bonus.ToString("N0", CultureInfo.InvariantCulture)} quick-delivery bonus" : "") + (wb.Late ? " (late)" : "");
                if (total > 0)
                    result.Commands.Add(new PayCommand { Amount = total, WaybillId = wb.Id, Memo = $"{car.Mark} {LoadName(wb.LoadId)} to {dest.Name}{extras}" });
                AddLedger("freight", wb, total, $"{car.Mark} {LoadName(wb.LoadId)} {wb.Origin} → {wb.Dest}{extras}");
                State.Stats.Delivered++;
                State.Stats.Revenue += total;
                Notify($"{car.Mark} spotted at {dest.Name}: ${total.ToString("N0", CultureInfo.InvariantCulture)}{extras}");
            }
            wb.State = dest is Interchange ? WaybillState.AwaitingPickup : WaybillState.Servicing;
            wb.Since = Now;
            car.Progress = 0.0;
            Info($"{wb.Id} {car.Mark} spotted at {dest.Id}, now {wb.State}");
        }

        /// <summary>Loading or unloading finished: apply the cargo once and write the successor waybill.</summary>
        private void Complete(CarRecord car, Waybill wb, IndustryComponent component)
        {
            var load = Data.Loads[component.LoadId];
            var observed = Observation(car.Guid)?.Cargo;
            var unloading = component.Type == ComponentType.Unloader;
            var expected = unloading ? load.DvCargo : null;
            if (!string.Equals(observed, expected, StringComparison.Ordinal))
            {
                Void(wb, $"cargo mismatch at {component.FullId}: expected {expected ?? "empty"}, found {observed ?? "empty"}", writeOff: false);
                RouteHome(car, LoadForCargo(observed), component.FullId, paid: false);
                return;
            }
            if (unloading)
                result.Commands.Add(new UnloadCargoCommand { Guid = car.Guid, DvCargo = load.DvCargo });
            else
                result.Commands.Add(new LoadCargoCommand { Guid = car.Guid, DvCargo = load.DvCargo });
            State.Waybills.Remove(wb.Id);
            car.WaybillId = null;
            car.Progress = 0.0;
            car.LoadId = unloading ? null : component.LoadId;
            Info($"{wb.Id} {car.Mark} {(unloading ? "unloaded" : "loaded")} at {component.FullId}");
            RouteHome(car, car.LoadId, component.FullId, paid: true);
        }

        /// <summary>Writes an outbound waybill to the car's home interchange.</summary>
        private void RouteHome(CarRecord car, string loadId, string origin, bool paid)
        {
            var home = Data.Interchange(car.Home) ?? Data.Interchanges.FirstOrDefault();
            if (home == null)
                return;
            car.Home = home.Id;
            car.LoadId = loadId;
            var originDest = Data.Destination(origin) ?? home;
            var pay = paid ? Math.Round(Revenue.ForMove(Data, originDest, home, loadId) * PayFactor((originDest as IndustryComponent)?.Industry)) : 0.0;
            NewWaybill(car, loadId != null ? WaybillKind.OutboundLoad : WaybillKind.OutboundEmpty, loadId, originDest.Id, home.Id, pay);
        }

        private Waybill NewWaybill(CarRecord car, WaybillKind kind, string loadId, string origin, string dest, double pay)
        {
            var wb = new Waybill
            {
                Id = $"MB-{State.NextWaybill++:D6}",
                CarGuid = car.Guid,
                Kind = kind,
                LoadId = loadId,
                Origin = origin,
                Dest = dest,
                Created = Now,
                Pay = pay,
                State = WaybillState.Open,
                Since = Now,
                GraceDays = Revenue.GraceDays(Revenue.Miles(Data, Data.Destination(origin), Data.Destination(dest))),
            };
            State.Waybills[wb.Id] = wb;
            car.WaybillId = wb.Id;
            car.Progress = 0.0;
            Plate(car);
            return wb;
        }

        private void Void(Waybill wb, string reason, bool writeOff)
        {
            State.Waybills.Remove(wb.Id);
            dwellSince.Remove(wb.CarGuid);
            if (State.Cars.TryGetValue(wb.CarGuid, out var car) && car.WaybillId == wb.Id)
            {
                car.WaybillId = null;
                car.Progress = 0.0;
            }
            State.Stats.Voided++;
            AddLedger("void", wb, 0.0, reason);
            Warn($"waybill {wb.Id} voided{(writeOff ? " (written off)" : "")}: {reason}");
        }

        /// <summary>A registered car that no longer exists: interchanged if it was waiting there, else written off.</summary>
        private void LoseCar(CarRecord car, string why)
        {
            if (car.WaybillId != null && State.Waybills.TryGetValue(car.WaybillId, out var wb))
            {
                if (wb.State == WaybillState.AwaitingPickup)
                    Interchanged(car, wb);
                else
                    Void(wb, $"{car.Mark} was lost: {why}", writeOff: true);
            }
            DropCar(car);
        }

        private void Interchanged(CarRecord car, Waybill wb)
        {
            State.Waybills.Remove(wb.Id);
            State.Stats.Interchanged++;
            AddLedger("interchanged", wb, 0.0, $"{car.Mark} left at {wb.Dest}");
        }

        private void DropCar(CarRecord car)
        {
            State.Cars.Remove(car.Guid);
            seen.Remove(car.Guid);
            dwellSince.Remove(car.Guid);
            rerouteOnSight.Remove(car.Guid);
        }

        private void Plate(CarRecord car)
        {
            var wb = car.WaybillId != null && State.Waybills.TryGetValue(car.WaybillId, out var w) ? w : null;
            var text = wb == null ? "" : Data.Destination(wb.Dest)?.Code ?? "";
            result.Commands.Add(new SetPlateCommand { Guid = car.Guid, Text = text });
        }

        // ------------------------------------------------------------ industries

        private void Tick(double dtMinutes)
        {
            var days = dtMinutes / OpsClock.MinutesPerDay;
            foreach (var industry in Data.Industries)
            {
                var storage = State.Industries[industry.Id].Storage;
                // The contract scales storage and every rate, as Railroader's contract multiplier does.
                var factor = ContractFactor(industry);
                foreach (var component in industry.Components)
                {
                    var cap = industry.Storage[component.LoadId] * factor;
                    var level = storage.TryGetValue(component.LoadId, out var v) ? v : 0.0;
                    var cars = ServicingCars(component);
                    if (component.Type == ComponentType.Unloader)
                    {
                        level = Math.Max(0.0, level - component.ConsumePerDay * factor * days);
                        var budget = component.UnloadPerDay * factor * days;
                        foreach (var (car, _) in cars)
                        {
                            var amount = Math.Min(budget, Math.Min(1.0 - car.Progress, cap - level));
                            if (amount <= 0.0)
                                break;
                            car.Progress += amount;
                            level += amount;
                            budget -= amount;
                        }
                    }
                    else
                    {
                        level = Math.Min(cap, level + component.ProducePerDay * factor * days);
                        foreach (var (car, _) in cars)
                        {
                            var amount = Math.Min(component.LoadPerCarPerDay * factor * days, Math.Min(1.0 - car.Progress, level));
                            if (amount <= 0.0)
                                continue;
                            car.Progress += amount;
                            level -= amount;
                        }
                    }
                    storage[component.LoadId] = Math.Max(0.0, Math.Min(cap, level));
                    foreach (var (car, wb) in cars)
                    {
                        if (car.Progress >= 1.0 - 1e-7)
                        {
                            car.Progress = 1.0;
                            Complete(car, wb, component);
                        }
                    }
                }
            }
        }

        /// <summary>Cars being serviced at a component that stand on its spots now, furthest along first.</summary>
        private List<(CarRecord car, Waybill wb)> ServicingCars(IndustryComponent component) =>
            State.Waybills.Values
                .Where(w => w.State == WaybillState.Servicing && w.Dest == component.FullId && IsAtDestination(w))
                .Select(w => (car: State.Cars[w.CarGuid], wb: w))
                .OrderByDescending(p => p.car.Progress)
                .ThenBy(p => p.wb.Id, StringComparer.Ordinal)
                .ToList();

        // ------------------------------------------------------------ interchange services

        private void RunServices()
        {
            foreach (var x in Data.Interchanges)
            {
                var st = State.Interchanges[x.Id];
                if (st.Pending == null && Now + Epsilon >= st.NextServiceAt)
                    Plan(x, st);
                if (st.Pending != null && (st.Pending.LastAttempt == null || Now - st.Pending.LastAttempt.Value + Epsilon >= RetryMinutes))
                    Attempt(x, st);
            }
        }

        /// <summary>Rebuilds the interchange's orders (they are never saved), as Railroader does at each service.</summary>
        private void Plan(Interchange x, InterchangeState st)
        {
            var service = new PlannedService { Id = $"SV-{State.NextService++:D5}", DueAt = Now };
            var components = Data.Components.Where(c => ShouldOrder(c) && Data.ServingInterchange(c) == x).OrderBy(c => c.FullId, StringComparer.Ordinal).ToList();
            rng.Shuffle(components);
            var summary = new List<string>();
            var counts = new Dictionary<IndustryComponent, int>();
            foreach (var c in components)
            {
                var n = OrderCount(c);
                counts[c] = n;
                if (n > 0)
                    summary.Add($"{n} {(c.Type == ComponentType.Unloader ? LoadName(c.LoadId) : "empty")} for {c.Code}");
            }
            // Cars are placed in plan order until the interchange is full, so the
            // plan takes one car per ordering component in turn: a component with
            // many orders (Connelly Creek's pulpwood empties) cannot fill the
            // interchange before every other customer has a car.
            for (var round = 0; round < MaxOrderPerComponent; round++)
            {
                foreach (var c in components)
                {
                    if (counts[c] > round)
                        service.Cars.Add(PlanCar(service, x, c));
                }
            }
            st.Pending = service;
            Info($"service {service.Id} at {x.Id} ({OpsClock.Format(State.Clock, Now)}): {(summary.Count == 0 ? "no orders" : string.Join(", ", summary))}");
        }

        /// <summary>
        /// Railroader's ordering rules (operations-design.md, 4.2; RR
        /// IndustryUnloader.OrderCars and IndustryLoaderBase.OrderCars):
        /// consumers order loads while half the storage shortfall is not
        /// covered; producers order empties while what they can ship (storage
        /// plus production until the next daily service, at least a carload once
        /// storage passes 90% with no empties coming) is not covered. Cars
        /// already en route or being serviced count by the share of a carload
        /// still to move. Storage caps and rates are scaled by the contract.
        /// </summary>
        public int OrderCount(IndustryComponent c)
        {
            if (!ShouldOrder(c))
                return 0;
            var want = Demand(c, OpsClock.NextDailyAfter(State.Clock, Now, Settings.ServeHour));
            return Math.Max(0, Math.Min(MaxOrderPerComponent, (int)Math.Ceiling(want - 1e-6)));
        }

        /// <summary>The carloads a component still wants ordered; see <see cref="OrderCount"/>.</summary>
        public double Demand(IndustryComponent c, double horizonEnd)
        {
            var factor = ContractFactor(c.Industry);
            var cap = c.Industry.Storage[c.LoadId] * factor;
            var level = Storage(c.Industry.Id, c.LoadId);
            var unloader = c.Type == ComponentType.Unloader;
            var pending = 0.0;
            foreach (var wb in State.Waybills.Values)
            {
                if (wb.Dest != c.FullId || wb.State == WaybillState.AwaitingPickup || wb.IsLoad != unloader)
                    continue;
                pending += 1.0 - (State.Cars.TryGetValue(wb.CarGuid, out var car) ? car.Progress : 0.0);
            }
            foreach (var st in State.Interchanges.Values.Where(s => s.Pending != null))
                pending += st.Pending.Cars.Count(p => p.Dest == c.FullId);
            if (unloader)
                return (cap - level) / 2.0 - pending;
            var horizonDays = Math.Max(0.0, horizonEnd - Now) / OpsClock.MinutesPerDay;
            var shippable = level + c.ProducePerDay * factor * horizonDays;
            if (level > 0.9 * cap && pending < Epsilon)
                shippable = Math.Max(1.0, shippable);
            return shippable - pending;
        }

        private PlannedCar PlanCar(PlannedService service, Interchange x, IndustryComponent c)
        {
            var carClass = Data.CarClasses[rng.Pick(c.CarClasses)];
            var loaded = c.Type == ComponentType.Unloader;
            var loadId = loaded ? c.LoadId : null;
            return new PlannedCar
            {
                Token = $"{service.Id}-{service.Cars.Count + 1:D2}",
                ClassId = carClass.Id,
                Livery = rng.Pick(carClass.Liveries),
                Mark = $"{rng.Pick(x.Roads)} {rng.Next(1000, 100000)}",
                LoadId = loadId,
                Kind = loaded ? WaybillKind.InboundLoad : WaybillKind.InboundEmpty,
                Dest = c.FullId,
                Pay = Math.Round(Revenue.ForMove(Data, x, c, loadId) * PayFactor(c.Industry)),
            };
        }

        private void Attempt(Interchange x, InterchangeState st)
        {
            var p = st.Pending;
            p.Attempts++;
            p.LastAttempt = Now;
            var removal = p.Removed ? new List<CarRecord>() : CarsToRemove(x);
            var remaining = State.Cars.Values.Where(c => removal.All(r => r.Guid != c.Guid) && IsOn(Observation(c.Guid), x.SpotList)).ToList();
            var cuts = Pack(x, p.Cars, remaining, out var unplaced);
            if (removal.Count == 0 && cuts.Count == 0)
            {
                // Nothing for the executor to do: either nothing was ordered, or nothing fits.
                p.Unfilled += unplaced;
                p.Cars.Clear();
                Finish(x, st);
                return;
            }
            var command = new ServiceCommand { ServiceId = p.Id, Interchange = x.Id };
            command.RemoveGuids.AddRange(removal.Select(c => c.Guid));
            command.Cuts.AddRange(cuts);
            attemptCuts[p.Id] = cuts;
            result.Commands.Add(command);
        }

        /// <summary>Cars spotted and paid at this interchange, standing on its spots, free of locomotives.</summary>
        private List<CarRecord> CarsToRemove(Interchange x) =>
            State.Waybills.Values
                .Where(w => w.State == WaybillState.AwaitingPickup && w.Dest == x.Id)
                .Select(w => State.Cars[w.CarGuid])
                .Where(c =>
                {
                    var o = Observation(c.Guid);
                    return IsStationaryOn(o, x.SpotList) && !o.CoupledToLoco;
                })
                .ToList();

        /// <summary>
        /// Fits planned cars into the interchange's spots in order: each spot
        /// takes one cut in its largest free stretch, within capacityFactor ×
        /// its length less the cars already on it (Railroader's capacity rule).
        /// </summary>
        internal List<ServiceCut> Pack(Interchange x, List<PlannedCar> planned, List<CarRecord> remaining, out int unplaced)
        {
            var cuts = new List<ServiceCut>();
            var queue = new List<PlannedCar>(planned);
            foreach (var spot in x.SpotList)
            {
                if (queue.Count == 0)
                    break;
                var occupied = new List<(double from, double to)>();
                var occupiedLength = 0.0;
                foreach (var car in remaining)
                {
                    var o = Observation(car.Guid);
                    var spans = new[] { o?.Front, o?.Rear }.Where(b => b?.Track == spot.RuntimeName).Select(b => b.Span).ToList();
                    if (spans.Count == 0)
                        continue;
                    var half = CarLength(car.ClassId) / 2.0;
                    occupied.Add((spans.Min() - half - CarGap, spans.Max() + half + CarGap));
                    occupiedLength += CarLength(car.ClassId);
                }
                var (gapFrom, gapTo) = LargestGap(spot.From, spot.To, occupied);
                var budget = Math.Min(x.CapacityFactor * spot.Length - occupiedLength, gapTo - gapFrom - CarGap);
                var cut = new ServiceCut { Index = cuts.Count, Track = spot.RuntimeName, StartSpan = Math.Round(gapFrom + CarGap, 2) };
                var used = 0.0;
                foreach (var car in queue.ToList())
                {
                    var length = CarLength(car.ClassId) + CarGap;
                    if (used + length > budget + Epsilon)
                        break;
                    used += length;
                    cut.Cars.Add(car);
                    queue.Remove(car);
                }
                if (cut.Cars.Count > 0)
                    cuts.Add(cut);
            }
            unplaced = queue.Count;
            return cuts;
        }

        private static (double from, double to) LargestGap(double from, double to, List<(double from, double to)> occupied)
        {
            var best = (from: from, to: from);
            var cursor = from;
            foreach (var (a, b) in occupied.OrderBy(o => o.from))
            {
                if (a > cursor && a - cursor > best.to - best.from)
                    best = (cursor, Math.Min(a, to));
                cursor = Math.Max(cursor, b);
            }
            if (to - cursor > best.to - best.from)
                best = (cursor, to);
            return best;
        }

        private double CarLength(string classId) => Data.CarClasses.TryGetValue(classId ?? "", out var c) ? c.LengthM : 15.0;

        private void HandleService(ServiceResult r)
        {
            var x = Data.Interchanges.FirstOrDefault(i => State.Interchanges.TryGetValue(i.Id, out var s) && s.Pending?.Id == r.ServiceId);
            if (x == null)
            {
                Warn($"result for unknown service {r.ServiceId}; ignored");
                return;
            }
            var st = State.Interchanges[x.Id];
            var p = st.Pending;
            if (!r.Executed)
            {
                if (r.DeferredReason != p.LastDeferral)
                    Info($"service {p.Id} at {x.Id} delayed: {r.DeferredReason}");
                p.LastDeferral = r.DeferredReason;
                return;
            }
            p.LastDeferral = null;
            var removed = 0;
            foreach (var guid in r.RemovedGuids)
            {
                if (!State.Cars.TryGetValue(guid, out var car))
                    continue;
                if (car.WaybillId != null && State.Waybills.TryGetValue(car.WaybillId, out var wb))
                    Interchanged(car, wb);
                DropCar(car);
                removed++;
            }
            p.Removed = true;
            attemptCuts.TryGetValue(p.Id, out var cuts);
            cuts = cuts ?? new List<ServiceCut>();
            var placed = 0;
            foreach (var pair in r.Placed)
            {
                var planned = p.Cars.FirstOrDefault(c => c.Token == pair.Key);
                if (planned == null)
                {
                    Warn($"service {p.Id} placed unknown token {pair.Key}");
                    continue;
                }
                p.Cars.Remove(planned);
                if (pair.Value == null || State.Cars.ContainsKey(pair.Value))
                {
                    Error($"service {p.Id} reported a bad or duplicate GUID for {planned.Mark}");
                    continue;
                }
                AddCar(planned, pair.Value, x);
                placed++;
            }
            var retry = new HashSet<string>(StringComparer.Ordinal);
            foreach (var failure in r.FailedCuts)
            {
                var cut = cuts.FirstOrDefault(c => c.Index == failure.Key);
                if (cut == null)
                    continue;
                var waiting = cut.Cars.Where(c => p.Cars.Contains(c)).ToList();
                if (failure.Value == CutFailure.DoesNotFit && waiting.Count > 0)
                {
                    // Railroader shrinks a cut that does not fit by a quarter.
                    var drop = Math.Max(1, (int)Math.Ceiling(waiting.Count * 0.25));
                    waiting = waiting.Take(waiting.Count - drop).ToList();
                }
                foreach (var c in waiting)
                    retry.Add(c.Token);
                Info($"service {p.Id}: cut {cut.Index} on {cut.Track} {failure.Value}; {waiting.Count} cars wait for a retry");
            }
            p.Unfilled += p.Cars.Count(c => !retry.Contains(c.Token));
            p.Cars.RemoveAll(c => !retry.Contains(c.Token));
            if (placed > 0 || removed > 0)
                Notify($"{x.Name}: {placed} cars arrived, {removed} left");
            if (p.Cars.Count > 0)
            {
                p.LastAttempt = Now;
                return;
            }
            Finish(x, st);
        }

        private void AddCar(PlannedCar planned, string guid, Interchange x)
        {
            var car = new CarRecord
            {
                Guid = guid,
                ClassId = planned.ClassId,
                Livery = planned.Livery,
                Mark = planned.Mark,
                Home = x.Id,
                LoadId = planned.LoadId,
            };
            State.Cars[guid] = car;
            NewWaybill(car, planned.Kind, planned.LoadId, x.Id, planned.Dest, planned.Pay);
        }

        /// <summary>Closes a service and schedules the next: an extra one when orders went unfilled, else the daily one.</summary>
        private void Finish(Interchange x, InterchangeState st)
        {
            var p = st.Pending;
            var unfilled = p.Unfilled;
            st.LastService = p.DueAt;
            st.Pending = null;
            attemptCuts.Remove(p.Id);
            State.Stats.Services++;
            var daily = OpsClock.NextDailyAfter(State.Clock, Now, Settings.ServeHour);
            st.NextServiceAt = daily;
            if (unfilled > 0)
            {
                var extra = Math.Max(p.DueAt, Now) + Settings.ExtraServiceDelayHours * 60.0;
                if (extra < daily)
                    st.NextServiceAt = extra;
            }
            Info($"service {p.Id} at {x.Id} done{(unfilled > 0 ? $", {unfilled} orders unfilled" : "")}; next at {OpsClock.Format(State.Clock, st.NextServiceAt)}");
        }

        // ------------------------------------------------------------ helpers

        private string LoadForCargo(string dvCargo) =>
            dvCargo == null ? null : Data.Loads.Values.FirstOrDefault(l => l.DvCargo == dvCargo)?.Id;

        private string LoadName(string loadId) => loadId != null && Data.Loads.TryGetValue(loadId, out var l) ? l.Name.ToLowerInvariant() : "empty";

        private void AddLedger(string kind, Waybill wb, double amount, string memo)
        {
            State.Ledger.Add(new LedgerEntry { T = Now, Kind = kind, WaybillId = wb.Id, CarGuid = wb.CarGuid, Amount = amount, Memo = memo });
            var excess = State.Ledger.Count - Math.Max(1, Settings.LedgerLimit);
            if (excess > 0)
                State.Ledger.RemoveRange(0, excess);
        }

        private void Notify(string text) => result.Commands.Add(new NotifyCommand { Text = text });

        private void Info(string text) => result.Log.Add(new LogLine(LogLevel.Info, text));

        private void Warn(string text) => result.Log.Add(new LogLine(LogLevel.Warning, text));

        private void Error(string text) => result.Log.Add(new LogLine(LogLevel.Error, text));
    }
}
