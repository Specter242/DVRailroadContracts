# Changelog

## 0.1.11 (2026-11-09)

- In multiplayer, stations no longer spawn locomotives, so a new career has only its one DE2.
- In multiplayer, players other than the host can take and hand in jobs at the interchange offices. Their jobs to and from the interchanges need Interchange Yards 0.1.7.
- While days last less than 24 hours, the kiosk's Contracts list has a Day length row: its page says how to set 24-hour days and what sleeping costs.

## 0.1.10 (2026-11-08)

- Each contract's page counts its cars still to move, out, in and waste, and its Cars row lists them by what each needs next, with car numbers and jobs; your contracts are listed most cars to move first.

## 0.1.9 (2026-11-07)

- Sell the vehicles you bought back at the kiosk's Sell vehicles: half what you paid, less what its repairs and refills cost, never less than a tenth. A wreck sells where it stands.
- A vehicle you buy is always new: it no longer takes the number and paint of one you cleared.

## 0.1.8 (2026-11-06)

- The short moves within a station, spotting cars at an industry and pulling its empties into storage, are shunting jobs: their papers say Shunting, and their IDs have SL or SU.
- Industries work each cargo where it's handled: at the Steel Mill, steel under the portal crane and deliveries at the factory dock, and likewise at the other industries' cranes, docks, silos, racks and log piles. Cars already being loaded or unloaded finish where they are.
- The interchanges are stations of their own, West Interchange (WI) and Northeast Interchange (NEI), with their own jobs, papers and booklets at their offices. Needs Interchange Yards 0.1.5.

## 0.1.7 (2026-11-06)

- Multiplayer: the job booklets at the interchange offices come out for any player there, not only the host.

## 0.1.6 (2026-11-05)

- Contract cars left on a station's tracks no longer disappear when you drive away. Persistent Jobs' "Suspend far away cars" leaves them alone, and brings back the other cars it put away as you return.
- The kiosk's "Cars without jobs" goes a page at a time: up and down turn the pages, and the title says which page you're on.

## 0.1.5 (2026-11-04)

- Coaches cost a third of what they did: a train of 2, 4 or 6 is $40,000, $80,000 or $120,000.
- Custom locomotives and coaches are priced like the stock ones of their kind, by weight, instead of by their own repair values, which ranged from far too cheap to over a million.
- A custom locomotive whose mod leaves its tender out of its set (the C-16) is sold with the tender.

## 0.1.4 (2026-11-04)

- Works with Career Manager API: Contracts is on its kiosk menu, last, after Stats.
- A car set out for pickup and then moved off the interchange is billed again at the next 06:00 service, instead of waiting there for good.
- The kiosk's Contracts list has a "Cars without jobs" row that says which contract cars have no job and why.
- The 06:00 service places its cars over a few frames instead of all at once, so the game hitches much less when it runs.
- Industries work their own cars. A car that reaches its industry gets a short job to spot it on the industry's track; the industry then loads or unloads it over time, faster at higher contract levels. A finished car goes out at once, joining the outbound job for the same place if it hasn't been taken yet. The kiosk's "Cars without jobs" says when each car will be ready.
- The mines' scrap gets loaded: before, its empties waited at the mine for good.

## 0.1.3 (2026-11-03)

- Buying a vehicle shows the whole result on the kiosk. One you can't afford is refused at once, with its price, your cash and how much you're short.
- Taking a contract says when its cars start coming: from 06:00 the morning after it starts at midnight, through the interchanges.
- Locomotives and coaches you buy arrive on a free track at the nearest interchange, like every other car. Bring coaches to a platform to board.

## 0.1.2 (2026-11-03)

- Quick-delivery bonus: from level 2, a contract job done within a day of its waybill pays 4, 6, 8 or 10% more (half on the second day, a quarter on the third), printed with the job's pay.
- Lowering or ending a contract costs a penalty, shown at the kiosk before you confirm. Three poor days in a row take a contract down two levels, with the penalty; a level 1 or 2 contract ends. A penalty your wallet can't cover is owed and taken as cash comes in.

## 0.1.1 (2026-11-03)

- Unity Mod Manager now checks for new versions and can install them.

## 0.1.0 (2026-11-03)

First release.

- Contracts with the stock map's industries, taken and managed at any station's career manager kiosk, in a Contracts menu of their own. A contract runs all of an industry's traffic: its products out, its materials in, its waste out.
- Cars come and go only through the two interchanges (Interchange Yards). Missing empties are ordered there; finished loads leave there.
- Contracts start at level 1 and grow a level at a time, once their performance has been good for three days.
- A bridge contract between the two interchanges: foreign loads arriving at one, bound for the other, both ways.
- Passenger contracts for the cities with Passenger Jobs installed: a daily timetable of trains run with coaches you own.
- A new career starts empty: one DE2 and the Manual Service license. Stations spawn no cars, jobs or locomotives; locomotives and coaches are bought at the kiosk, and a locomotive you buy comes with its license.
- A contract needs the licenses its cargo asks for. Shunting and Logistical Haul come with every career, and contract jobs don't count toward the concurrent job limit.
- Set the day duration to 24 hours: contracts are counted in game days.
