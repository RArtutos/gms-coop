# GMS Co-op: architecture

This guide is for maintainers. It describes the co-op mod for Gold Mining Simulator: Unity 2020.3, Mono, BepInEx 5 with HarmonyX 2.10, net472. Players see the README (Spanish) instead.

Wire protocol: **12** (`Net.Protocol`, plugin version 0.13.0, launcher 0.13.0). Both PCs must run the same build. A peer with another protocol is refused at Hello, with a message that says why.

## 1. Big picture

- **Topology: star.** The host is authoritative for the world and relays every client message to the other clients. Clients only ever talk to the host.
- **Transports** (`Transport.cs`, implementing `ITransport`):
  - **Steam P2P** (`SteamTransport`):
    - channel 0 is reliable and ordered;
    - channel 1 is unreliable, for packets of 1200 bytes or less.
  - **LAN TCP** (`LanTransport`):
    - one socket per client, with a worker write thread;
    - "unreliable" packets use TCP too.
- **Identity:**
  - Transport peer id: a SteamID, or a LAN connection id.
  - `Net.LocalId`: the id each player puts inside game messages.
    - Steam: the SteamID, so it equals the transport peer id.
    - LAN: random.
  - The host learns each peer's LocalId from its Hello (`Net.PeerLocalId`, `Net.LocalIdOf`).
- **Joining = receiving the host's world** (`SaveTransfer.cs`):
  1. The host saves with the game's own QuickSave.
  2. It streams the slot folder in 64 KB chunks.
  3. The client writes it to the slot `Coop_Host` and loads it with the game's normal load (`RequestLoad`).
  4. The client re-indexes its systems, then sends `Loaded`.
  5. The host sends the catch-up (section 4).
- **Features:** every feature is one `*Sync.cs` static class. Each has:
  - `Tick()` (some also `FixedTick()`), called from `Plugin`;
  - `Receive(...)`, called from `Net.Handle`;
  - its Harmony patches, as nested classes. Targets are looked up with `AccessTools`; a missing target logs a warning and `Prepare` returns false, so a game update disables one feature instead of breaking `PatchAll`.

## 2. Files

### Core

| File | Role |
|---|---|
| `Plugin.cs` | BepInEx entry point: keys, the tick order (section 6), the overlay text, GPU limit for a second local instance. Each system is ticked in isolation: an exception is logged at most once per 10 s per system, and the other systems keep running. |
| `Net.cs` | Session: Hello / Bye / Ping, peers, keep-alive and timeouts (30 s, or 120 s while a world is in transit), link quality, dispatch (`Handle`), relay rules, the world gates (`WorldLive`). |
| `Transport.cs` | Steam P2P and LAN TCP with lanes (up to 4 TCP connections per peer: 0 session, 1 and 3 snapshots, 2 bulk). |
| `TransportCore.cs` | `Packet`, `ITransport`, `ILaneTransport`. |
| `DirectLink.cs` | `HybridTransport` over the LAN/tunnel transport: per-channel sequencing, lanes, and the automatic direct UDP link (STUN, hole punching, secure reliable UDP). See section 11. |
| `SaveTransfer.cs` | World transfer, host reload, the client's return to its own game, save redirection, the catch-up after a join, and the world journal. |
| `SteamLobby.cs` | Friends-only lobby, invites, rich presence "Join game", `+connect_lobby`. |
| `Overlay.cs` | uGUI status panel in the top-left corner. |
| `MainThread.cs` | Queues work from worker threads (LAN connect callback) onto the main thread. |
| `DebugDump.cs` | Ctrl+F12: logs the humanoid models and animation clips. Development only. |
| `SyncLogic.cs` | Pure logic with no Unity types: `SyncClock` (snapshot interpolation clock), ownership and epoch rules, pose buffers. |
| `SyncCore.cs` | Pure helpers: `PoseBuffer`, `Predict`, `HostLink`, `AckTracker`, `ReplyQueue`, `SeqWindow`, `LatenessEstimator`. |
| `GameState.cs` | `GameState.IsLoading`. |

### Features

| Area | Files | Message |
|---|---|---|
| Objects | `ObjectSync.cs` (loose physics objects, holders, bucket stacks, ropes / hooks / fuel pistols, generic create / destroy) | 12 |
| Objects | `PurchaseSync.cs` (shop, vehicles, trailers, lots, laptop tabs, rentals, sales, parcel reset, workers hired) | 10 |
| Vehicles | `MachineSync.cs` (driving, trailers, teleports, ids) | 6 |
| Vehicles | `MachinePartsSync.cs` (arms, wheels, engine, fuel / wear, light trailer) | 11 and 6 |
| Vehicles | `BedSync.cs` (truck bed cargo) | 13 |
| Vehicles | `PassengerSync.cs` | 12, sub 4 |
| Player and world | `PlayerSync.cs` + `Avatar.cs` | 2 |
| Player and world | `TerrainSync.cs` | 3, 4, and 14 subs 4 / 9 |
| Player and world | `TutorialSync.cs` | 7 |
| Player and world | `GoldSync.cs` (ledger, clock, seasons, workers, statistics) | 14 |
| Player and world | `ContainerSync.cs`, `DirtSync.cs`, `WorldSync.cs`, `FuelSync.cs` | 14 subs |
| Player and world | `SessionLifecycle.cs` (game settings snapshot and restore) | none |
| Player and world | `InteractionGuard.cs` (never leaves the local player locked by an interaction another player broke: shovel holders, separator, machine enter / exit, gold panning, ladders, long input blocks) | none |
| Player and world | `CashSync.cs` (legacy shim) | 5 |

## 3. Messages

The first byte of every packet is the `Msg` id. For ids 7, 10, 12, 13 and 14, the second byte is a sub-kind. For id 6, an int32 comes first: an id of 0 or more is a machine pose, and a negative value is a sub-message.

### Columns

- **Rel.:** R = reliable, U = unreliable (Steam channel 1).
- **Relay:** whether the host relays a client's copy of the message to the other clients.
- **Catch-up:** how a joiner gets what happened while the world was in transit:
  - journal = replayed after `Loaded`;
  - full = full state sent after `Loaded`;
  - live = continuous stream;
  - none = transient.

| Id | Name | Rel. | Relay | Catch-up | Notes |
|---|---|---|---|---|---|
| 1 | Hello | R | never | none | name, LocalId, protocol, `Application.version` |
| 2 | PlayerPose | U | yes | live | timestamp f64 (protocol 9) |
| 3 / 4 | TerrainHeights / TerrainAlphas | R | yes | TerrainSync journal | absolute writes, idempotent. Voxel messages (kind byte on msg 3) carry arrays by id: 0 gold, 1 + ResourceType resource volume, 0xFF soft-dirt floor (`GoldTerrainDigger._softDirt`, "SoftDirt"); an unknown id is skipped. Compacted per-tile queues are split into messages of about 256 KB raw (row bands for heights / splats, index runs for voxels), well below Steam's 1 MB reliable limit. |
| 5 | Cash | R | yes | none | legacy, ignored with a warning |
| 6 | MachinePose | U / R | yes | world journal (R only, except sub -5) | exit pose R; -2 hitch, -3 state, -4 left, -5 id map (host to the joiner, sent before the save), -6 teleport, -7 light trailer, -8 rest pose of a parked machine/trailer (host to clients, R). Flag 4 on a pose = coasting (out of the machine, still rolling). Each packet carries a world epoch byte. Pose packet 64 bytes (110 with a towed trailer), timestamp f64 (protocol 9). |
| 7 | Tutorial | R | yes, except 3 / 4 | full (`SendFullState`) | 1 StepState, 2 MissionState, 3 StepRequest (to the host only), 4 MissionRequest (to the host only), 5 CondMet |
| 8 | SaveChunk | R | never | none | host to one client |
| 9 | SaveControl | R | never | none | 1 Preparing, 2 Loaded, 3 Resend, 4 Failed, 5 CatchUp |
| 10 | Purchase | R | yes | PurchaseSync journal (dedup by origin + seq) | 1 Spawn, 2 RentBuyout, 3 RentReturn, 4 Lot, 5 Tabs, 6 Worker, 7 Sell, 8 ParcelReset |
| 11 | MachineParts | U / R | yes | world journal (R only) | |
| 12 | Objects | U / R | yes, except 9 | full (key-sync dump, pushed on `Loaded`) and journal (sub 4) | 0 Grab, 2 Drop, 4 Passenger, 5 Poses (U), 6 Rest, 7 Claim, 8 Attach, 9 SyncReq (to the host only), 10 Sync (host to one client), 11 StackOp, 12 Destroy, 13 Create, 14 Adopt, 15 Rope. Stamps of Poses / Drop / Rest are f64 (protocol 9): Poses header 19 bytes, then about 45 bytes per object (key length + 30), at most 1150 bytes per packet. |
| 13 | Bed | R | yes | world journal | 0 Freeze, 1 Unfreeze, 2 Request |
| 14 | Economy | R / U | yes, except the to-the-host subs | full (`GoldSync.SendCurrent`), world journal (sub 15), WorldSync journal (subs 20 / 22) | see below |
| 15 | PlayerLeft | R | never | none | sent by the host |
| 16 | Ping | U | never | none | keep-alive and RTT / loss probe |
| 17 | Bye | R | never | none | reason text |

### Msg 14 sub-kinds

| Sub | Use |
|---|---|
| 0 | ledger delta (to the host only). A frame that spends gold / diamonds / magnetite, and a loan operation, travel alone; the host refuses such a delta whole when its balance no longer covers it (`Predict.Overdraws`): acknowledged without applying, sub 26 first |
| 1 | ledger state |
| 2 | container delta (to the host only) |
| 3 | container state |
| 4 | parcel reset (TerrainSync journal) |
| 5, 6, 7 | mud / rent gold / minerals forward (to the host only) |
| 8 | dirt clods (U) |
| 9 | drill point (TerrainSync journal) |
| 10 | container request (to the host only) |
| 11 | clock (starts with an f64 send stamp, protocol 9) |
| 12 | sleep (to the host only) |
| 13 | season end (to the host only) |
| 15 | worker gone (world journal) |
| 16 | statistics |
| 17 | nugget hint |
| 18 | world flags |
| 19 | explosion (transient) |
| 20 | use / switch (WorldSync journal; the joiner holds live events until its end marker, only when `SaveTransfer.HoldWorldMessages` was set at sync start) |
| 21 | forge |
| 22 | paint (WorldSync journal) |
| 23 | fuel state |
| 24 | fuel purchase (to the host only) |
| 25 | loaded areas (every PC: author u64, isHost bool, count u16, scene names). World processing in a streamed area runs on the host if it has the area loaded, else on the client with the lowest LocalId that has it (`AreaAuthority`: a PC takes over only 3 s after announcing, gives up at once; the host takes an area no client lists at once) |
| 26 | refused spend (host to all: stream u64, seq u32): the client runs the action's undo (smelting cancelled, forge level back) |
| 27 | forge bar claim (client to the host: stream u64, claim u32) |
| 28 | forge bar grant (host to all: stream u64, claim u32, granted bool, amount decimal): one collector per remelt |

Sub 14 is free.

### Rules

- **Relay** (`Net.Handle` / `Net.HostOnly`):
  - The host relays every client world message to the other clients, with the same reliability, **before** it applies the message itself: an exception in a handler can never stop the relay, and a correction the host's handler sends always arrives after the relayed original.
  - It does not relay the "to the host only" requests (see `HostOnly`): every client ignores a relayed copy of those.
  - Hello, Ping, Bye, PlayerLeft and messages 8 / 9 are never relayed.
- **Gates:**
  - On the host, a client's world messages are dropped until that client sent `Loaded` for its current transfer (`SaveTransfer.PeerReady`).
  - On a client, world messages from the host are dropped between its `Loaded` and the host's `CatchUp` (`SaveTransfer.HoldWorldMessages`; released after 30 s at the latest).
  - Each system additionally gates on `Net.WorldLive`.
- **Time stamps:** every stream that is played back with interpolation (vehicles, players, objects) stamps its packets with `Time.unscaledTimeAsDouble` as f64, and the receivers keep buffers, clock offsets and render times in double. A float game clock has only ~1 ms resolution after 2-3 hours of play (~4 ms after 8 h), which showed as judder that grew over a session.
- **Unreliable to joiners:** the host does not send unreliable broadcasts to a peer that is not ready yet. That peer would drop them, and they would only slow its world transfer.

### Adding a message

1. Take a free id or sub-kind.
2. Add the case to `Net.Handle` or to the owner's sub dispatcher.
3. Decide three things:
   - **Relay:** to the host only? Then add it to `HostOnly`.
   - **Join catch-up:** a journal entry in `SaveTransfer.Journaled`, an own journal (like TerrainSync / PurchaseSync / WorldSync), or a full state in the owner's `SendCurrent`. Never two of them for the same message.
   - **Reliability:** unreliable packets must stay under 1200 bytes on Steam.
4. Bump `Net.Protocol` whenever a layout changes.

## 4. Join and catch-up sequence

1. **Client:** Hello. **Host:** Hello back, `OnPeerJoinedHost`, Preparing.
2. **Host:**
   - `TryStartSave`: QuickSave (once the host has been in a world for at least 3 s).
   - `MachineSync.ResetIndex` sends the joiner the id map.
   - `ObjectSync.ResetIndex` records the save ordinals (a snapshot) instead of re-keying.
   - The terrain, purchase and world journals start.
3. **Host:** `CheckSaveFinished`:
   - packs the slot;
   - ties the peer to this snapshot and to the journal starts;
   - streams the chunks, with flow control: at most 512 KB queued, 8 chunks per frame.
4. **Client:**
   - receives the chunks, and asks only for missing ranges after a stall;
   - writes `Coop_Host`;
   - calls `RequestLoad`;
   - Settling (1 s): `MachineSync.ResetIndex` and `ObjectSync.ResetIndex`, then Idle (`WorldReady` = true);
   - sends `Loaded(transferId)` and starts holding the host's world messages.

   In the same frame its systems index the world. MachineSync matches its ids against the host's id map. ObjectSync keys objects by save ordinal.
5. **Host, on `Loaded`:**
   - the peer is now ready;
   - it sends, in this order:
     1. `CatchUp`;
     2. the TerrainSync pending queue (per tile, compacted to the newest value per cell: edits for tiles the host has not loaded, not in its save), then the TerrainSync journal;
     3. the PurchaseSync journal;
     4. the WorldSync journal (switches, paint), then its end marker;
     5. the world journal;
     6. Tutorial full state;
     7. `GoldSync.SendCurrent`: ledger, containers, world flags, forge, fuel;
     8. `ObjectSync.SendCatchUp`: the key-sync dump and the rope shapes.
6. **Client:** stops holding at `CatchUp` and applies everything in order.
   - ObjectSync renames its save ordinals to the host's keys when the dump completes. If the first dump packet comes before its own 2 s index settle time, it builds the index at once (SaveTransfer already let the world settle for 1 s), so the pushed dump is never dropped.
   - It asks for the dump itself (SyncReq) only if the dump does not arrive within 4 s of building its index.

The world journal (`SaveTransfer.JournalWorld`, fed by `Net.Broadcast` on the host) keeps these reliable messages:

- Msg 6 (except the id map);
- Msg 11;
- Msg 13;
- Msg 12 sub 4;
- Msg 14 sub 15.

Switches and paint (Msg 14 subs 20 / 22) are journaled by WorldSync itself (`ClearJournal` / `AssignJournal` / `ReplayJournal`, called by SaveTransfer next to the PurchaseSync calls), with author + sequence dedupe and an end marker.

It is limited to 16 MB and trimmed when no peer needs it any more. The client's hold makes every replay exact: nothing is applied both live and from a journal.

### Host world changes

On a host QuickLoad, a load from the menu, a new game or the main menu (`TrackHostWorld`):

- clients get Preparing at once;
- the journals are dropped;
- MachineSync bumps its world epoch, and old packets are ignored;
- every client gets the new world 3 s after the host is in it.

### Client world changes

- QuickLoad of the shared world is refused.
- Loading one of its own slots, or going to the main menu, ends the session.
- When the session ends for any reason, the client gets back the slot it played before joining, or the main menu.
- While the host's world is loaded on a client, every save goes to `Coop_Host`.

## 5. Shared state: use these, do not copy them

| Need | Use |
|---|---|
| "World sync is live here" | `Net.WorldLive` (Active, `SaveTransfer.WorldReady`, not `GameState.IsLoading`) |
| Plays the host's world (no loading check) | `SaveTransfer.WorldReady` |
| Catch-up received (client) | `SaveTransfer.CaughtUp` |
| Game is loading | `GameState.IsLoading` |
| Session over / new session | `Net.Active`, and `Net.SessionId` (grows on every session start) |
| LocalId of a transport peer | `Net.LocalIdOf(peer)` (valid inside `OnPeerLeft` callbacks, which run before the peer is forgotten) |
| Who drives a machine | `MachineSync.LocallyDriven(m)` (false for a passenger), `IsRemoteDriven(id)`, `DriverOf(id)` |
| Who simulates a machine | `MachineSync.IsAuthority(m)` (local driver, else the remote driver's PC, else the host; the host also while the last driver gets out). `ContainerSync.MachineAuthority(m)` wraps it. |
| Trailers | `MachineSync.TowingMachine(t)`, `IsTrailerAuthority(t)` |
| Remote driver near a spot | `MachineSync.DrivesNear(player, pos, r)` (ObjectSync: no proximity / motion claim within 15 m of the machine the owner drives) |
| Overlay status | `Plugin.SetStatus(text, log = true)` (a repeat of the current text is not logged; progress lines pass `log: false`) |
| Why the last session ended | `Net.EndReason` (Spanish, null when the player left by itself) |
| Remote-apply flags (echo guards) | `PurchaseSync.Applying`, ObjectSync `_applyingRemote` / `_applyingAttach` (internal), `MachineSync._applyingTeleport` |
| Snapshot playback clock | `SyncClock` (`SyncLogic.cs`): ObjectSync and PlayerSync share it, so a held object and the hand that holds it play back with the same delay. Its lag follows the target proportionally (time constant 0.5 s, at most 10 % of real time), so playback speed does not flip between 0.9x and 1.1x. API in double seconds: `Feed(double stamp, double now, bool timing = true)`, `double RenderAt(double now)`, `ITimed.Stamp` is `double`; `SyncLogic.InsertByTime / DropPassed / Segment` take double times. MachineSync uses its own `VehiclePlayback`. |

A peer that leaves: `Net.OnPeerLeft` calls, in order:

1. `ObjectSync.OnPeerLeft` (the host takes over the leaver's objects);
2. `MachineSync.OnPeerLeft` (releases its drives and broadcasts sub -4);
3. `SaveTransfer.OnPeerLeft`;
4. `PlayerSync.RemoveGhostsFrom`.

The host then broadcasts PlayerLeft(LocalId), and every client runs `PlayerSync.RemovePlayer`. Other per-player state (ledger streams, acks, tutorial replies) expires by itself.

## 6. Tick order (`Plugin`)

**Update:**

| # | System | Why it is here |
|---|---|---|
| 0 | `Net.Poll` | receive and dispatch, keep-alive |
| 1 | `SaveTransfer` | re-index and Loaded happen before the systems tick |
| 2 | `PlayerSync` | |
| 3 | `TerrainSync` | |
| 4 | `GoldSync` | also `SessionLifecycle`, `ContainerSync`, `DirtSync`, `WorldSync` / `FuelSync` |
| 5 | `MachineSync` | also `BedSync` |
| 6 | `MachinePartsSync` | needs MachineSync's ids |
| 7 | `ObjectSync` | |
| 8 | `PassengerSync` | needs MachineSync's drivers |
| 9 | `TutorialSync` | |
| 10 | `PurchaseSync` | its queues wait for `ObjectSync.KeysReady` |

**FixedUpdate:** `MachineSync.FixedTick`, then `ObjectSync.FixedTick` (loads ride on or are tied to machines).

**LateUpdate:** the overlay text, 4 times a second.

## 7. Harmony patches

HarmonyX runs every prefix, even after one returned false; a false only skips the original method. A prefix that must not act when another skips the original checks that condition itself.

Below, a dash in the "Pre / Post" column means the patch has no prefix or no postfix. A "(skip)" after a prefix means it may return false and skip the original method.

### Game methods patched from more than one file

| Game method | File: patch | Pre / Post | Interplay |
|---|---|---|---|
| `CheckPointManagerBase.Add(SaveMe)` | ContainerSync, MachineSync, ObjectSync, WorldSync | each a postfix | Each only sets a flag or queues for its next tick. Order does not matter. |
| `MachineController.Update` | MachinePartsSync `MachineUpdatePatch` | prefix | applies the remote drive state |
| | PassengerSync `PatchUpdate` | prefix / postfix | `IsDriven` and `HasOperatorMode` off for a passenger, then restored |
| | MachineSync `PatchMachineUpdateKinematic` | postfix | keeps a remote-driven body kinematic |
| | | | All three are independent. |
| `GoldTerrainDigger.DigWithBox` / `TryMove` | ContainerSync `PatchTerrainDig` | prefix (skip) | blocks digging by machines another PC simulates |
| | TerrainSync `PatchVoxelWrites` | prefix | marks the tile for a voxel diff |
| | | | When ContainerSync skips, TerrainSync's prefix still runs, but returns at once while `ContainerSync.BlockDig > 0`. |
| `SeasonController.EndSeasonAndGoToNext` | GoldSync `PatchHostOnly` | prefix / finalizer | same file, coordinated |
| | GoldSync `PatchSeasonEnd` | prefix / postfix | same file, coordinated |
| `Finance.ReturnMachine` | GoldSync `PatchFinanceHostOnly` | prefix (skip) | skipped on clients |
| | PurchaseSync `PatchDailyRentalReturn` | prefix (skip) | Host only: sends RentReturn, and spares a machine another player drives. Disjoint conditions. |
| Vehicle `Teleport` overrides and `Depot.Teleport` / `DepotPlace.TransportGameObject` | MachineSync `PatchTeleport` | prefix (skip) / postfix | Postfix sends sub -6. It skips while `PurchaseSync.Applying`, so a replayed purchase or reset is not echoed. The trailer hitch patches skip then too. |
| | PurchaseSync `PatchDepotTeleport` / `PatchPlaceTransport` | prefixes | capture the purchase context |

### Every patch

| File | Patch | Target | Pre / Post | Purpose |
|---|---|---|---|---|
| BedSync | `PatchFreezeNow` | `TransportBed.FreezeNow` | prefix (skip) / postfix | the driver's cargo is authoritative |
| BedSync | `PatchUnfreeze` | `TransportBed.Unfreeze` | prefix (skip) / postfix | the driver's cargo is authoritative |
| BedSync | `PatchExitMachine` | `Pickup.ExitMachine` | prefix / postfix | cargo handover on exit |
| ContainerSync | `PatchSavedAdd` | `CheckPointManagerBase.Add` | - / postfix | container re-index |
| ContainerSync | `PatchProcessing` | `Update`, `FixedUpdate`, `LateUpdate`, `UpdateRare` of processing types (wash plants, shaker, elevator, hog pan...) | prefix / finalizer | only the authority processes; others simulate for show |
| ContainerSync | `PatchBeltClod` | `FrankensteinBelt.BeltDirt.UpdateDirtPosition` | - / postfix | no duplicate clods |
| ContainerSync | `PatchElevatorThrow` | `ConveyorElevatorBucket.ThrowOut` | prefix (skip) | no duplicate clods |
| ContainerSync | `PatchHints` | public `HUD` methods | prefix (skip) | throttle hints from foreign processing |
| ContainerSync | `PatchDiggingUpdate` | `DiggingController.Update` | prefix / finalizer | no digging by machines another PC simulates |
| ContainerSync | `PatchShovelUpdate` | `DiggingShovelController.Update` | prefix / finalizer | same, for blades |
| ContainerSync | `PatchTerrainDig` | `GoldTerrainDigger.DigWithBox`, `TryMove`, `SoftenTerrain`, `ShovelAddHeights` | prefix (skip) | the terrain block above |
| ContainerSync | `PatchCreateDirt` | `DiggingController.CreateVirtualDirt` | prefix (skip) | only the authority makes clods |
| ContainerSync | `PatchThrowOut` | `DumpTruckDirt.CheckThrowOut` | prefix (skip) | only the authority dumps the bed |
| DirtSync | `PatchLumpStart` | `VirtualVolumeDirt.Start` | - / postfix | clod sync |
| FuelSync | `PatchFuelBuy` | `FuelStationGUI.Submit` | prefix / postfix | fuel bought as a host operation |
| FuelSync | `PatchFuelOrder` | `FuelStationStationaryGUI.Submit` | prefix / postfix | fuel delivery orders |
| GoldSync | `PatchLoanActions` | `LaptopCredit.GetCredit`, `BankLoanGUI.TakeLoan` / `PayLoan` / `PartialPayLoan` | prefix / postfix | loan operations become ledger operations |
| GoldSync | `PatchFinanceHostOnly` | `Finance.GetCreditPart`, `ReturnMachine` | prefix (skip) | host only |
| GoldSync | `PatchAddMud` | `GameStatistics.AddMud` | prefix (skip) / postfix | nugget bonus and mud forwarded to the host |
| GoldSync | `PatchAddMinerals` | `GameStatistics.AddMinerals` | prefix (skip) | forwarded to the host |
| GoldSync | `PatchRentGold` | `Finance.AddGold` | prefix (skip) | forwarded to the host |
| GoldSync | `PatchEarnMoney` | `GameStatistics.EarnMoney` setter | prefix | statistics |
| GoldSync | `PatchDriveKm` | `GameStatistics.DriveKilometers` setter | prefix | statistics |
| GoldSync | `PatchStockSetters` | `Gsm.SetCash` / `SetGold` / `SetDiamonds` / `SetMagnetide`, `SumofGoldNuggets` setter | prefix (skip) | stock changes only through the ledger |
| GoldSync | `PatchHostOnly` | `Finance.CollectDailyData`, `SeasonController.EndSeasonAndGoToNext`, `SeasonRestoreGold.Submit`, `MachineController.WorkerRefuel`, `LaptopGlobalManager.Start` / `AfterDeserialize`, chest / new-save effects | prefix / finalizer | the host's economy code |
| GoldSync | `PatchRandomEvents` | `RandomEvents.InvokeRandomEvent` | prefix (skip) | host only |
| GoldSync | `PatchCollectSalary` | `Worker.CollectSalary` | prefix (skip) | the host pays |
| GoldSync | `PatchSeasonEnd` | `SeasonController.EndSeasonAndGoToNext` | prefix / postfix | a shared season end |
| GoldSync | `PatchRemelt` | `BlacksmithCraftGUI.StartGoldRemelt` | prefix / finalizer | client: the smelting spend travels alone; refused by the host, the remelt is cancelled |
| GoldSync | `PatchForgeUpgrade` | `BlacksmithUpgradeGUI.Upgrade` | prefix / finalizer | client: the magnetite spend travels alone; refused, the level goes back |
| GoldSync | `PatchForgeCollect` | `BlacksmithCraftGUI.OnEnable`, `OnDisable` | prefix / finalizer | client: remelted bars are handed out only after the host grants the collection (sub 27 / 28) |
| MachinePartsSync | `LightTrailerOpPatch` | `MobileLightTrailer` panel ops, `LightSegment.startRotate` / `stopRotate` | - / postfix | stream the light trailer |
| MachinePartsSync | `MachineUpdatePatch` | `MachineController.Update` | prefix | remote drive inputs |
| MachinePartsSync | `StopEnginePatch` | `MachineController.StopEngine` | prefix (skip) | the engine follows the remote driver |
| MachinePartsSync | `RepairPatch` | `CheckAndRepair.Repair` | - / postfix | repairs |
| MachinePartsSync | `BackhoeLocalInputPatch` | `Backhoe.Update`, `DoFixedUpdate` | prefix (skip) | no local input on remote backhoes |
| MachinePartsSync | `EngineRpmPatch` | `GetEngineRPM01`, `GetEngineLoad` overrides | - / postfix | remote rpm and load for sound |
| MachinePartsSync | `WheelVisualPatch` | `VPWheelCollider.UpdateVisualWheel` | prefix | remote wheels |
| MachinePartsSync | `AnimatedJointPatch` | `AnimatedJoint.FixedUpdate` | prefix (skip) | remote arm joints |
| MachineSync | `PatchSaveMeAdd` | `CheckPointManagerBase.Add` | - / postfix | index new machines |
| MachineSync | `PatchSaveMeRemove` | `CheckPointManagerBase.Remove` | prefix | unregister |
| MachineSync | `PatchTrailerConnect` | `Trailer.Connect` | - / postfix | hitch sync |
| MachineSync | `PatchTrailerDisconnect` | `Trailer.Disconnect`, `DisconnectInstantly` | prefix | unhitch sync |
| MachineSync | `PatchTeleport` | every vehicle and trailer `Teleport(Vector3, Quaternion, DepotPlace)` | prefix (skip) / postfix | teleport sync; refused for a remote-driven machine |
| MachineSync | `PatchSanityCheck` | `MachineController.SanityCheck` | prefix (skip) | not on remote-driven machines |
| MachineSync | `PatchMachineUpdateKinematic` | `MachineController.Update` | - / postfix | remote bodies stay kinematic |
| MachineSync | `PatchXmlRigidbodySerialize` | `XmlRigidbody.Serialize` | - / postfix | save the real `isKinematic` |
| MachineSync | `PatchPickupTurnOffKinematic` | `Pickup.TurnOffKinematic` | prefix (skip) | remote pickups stay kinematic |
| ObjectSync | `PatchHookRopeReactions` | `Hook_Rope.OnHookPicked`, `OnAttachFixed`, `Fuel_Rope.PistolAttach` | prefix (skip) | no local reactions during network applies |
| ObjectSync | `PatchFuelGuiOnRemoteAttach` | `ShopGUI.Show`, `FuelStationGUI.Show` | prefix (skip) | no purchase window on a remote attach |
| ObjectSync | `PatchRopeCanAttach` | `ShovelRopeHolder.CanAttach` | prefix (skip) | remote attaches always allowed |
| ObjectSync | `PatchSaveMeAdded` | `CheckPointManagerBase.Add` | - / postfix | generic create |
| ObjectSync | `PatchBucketCreateStack` | `Bucket.CreateStack` | prefix / finalizer | stack op capture |
| ObjectSync | `PatchBucketSlotAttach` | `BucketSlot.Attach` | prefix / finalizer | stack op capture |
| ObjectSync | `PatchBucketStackDetach` | `BucketStack.Detach` | prefix / finalizer | stack op capture |
| ObjectSync | `PatchBucketStackCombine` | `BucketStack.Combine` | prefix / finalizer | stack op capture |
| ObjectSync | `PatchGravityGunDrop` | `GravityGun.DropObject(bool)` | prefix (skip) | no drop during a remote attach / detach |
| ObjectSync | `PatchGravityGunPickOnRemoteHolder` | `GravityGun.PickObject(GameObject, bool)` | prefix (skip) | no pick for the local player during a remote attach / detach (`RopeTrommel.Detach`) |
| InteractionGuard | `PatchShovelHolder`, `PatchSmallShovelHolder` | `ShovelToolHolder.Unscrewing`, `ShovelSmallToolHolder.Unscrewing` | - / postfix (wrap) | watchdog: a killed shovel use frees input and gives the shovel back |
| InteractionGuard | `PatchDirtBoxDetach` | `HogPanDirtBox.OnDetach` | prefix / finalizer | the shovel holder stays active while in use |
| InteractionGuard | `PatchSeparator` | `BucketHolderMagnetiteSeparator.AddContentFromBucket` | - / postfix | watchdog for its input block |
| InteractionGuard | `PatchVehicleTransition` | `EnterVehicleController.CycleToCameras` / `CycleCamerasOut` / `InterpolateCameraToPath` | - / postfix (wrap) | watchdog: a killed enter / exit animation ends in free walk, input free |
| InteractionGuard | `PatchRemotePanAttach` | `GoldPan.Attach` | prefix (skip) | a remote pan attach does not start the local panning view |
| InteractionGuard | `PatchPickRemotePanning` | `GravityGun.PickOutlinedObject` | prefix (skip) | no grabbing the pan / tub another player pans with |
| ObjectSync | `PatchGravityGunPick` | `GravityGun.PickOutlinedObject` | prefix (skip) | no grabbing what another player holds |
| ObjectSync | `PatchToolGunPick` | `ToolGun.PickOutlinedObject` | prefix (skip) | same |
| PassengerSync | `PatchFixedUpdate` | `MachineController.FixedUpdate` | prefix | passenger: `IsDriven` off |
| PassengerSync | `PatchUpdate` | `MachineController.Update` | prefix / postfix | passenger: no handbrake or operator mode |
| PassengerSync | `PatchControlEngineStart` | `MachineController.ControlEngineStart` | prefix (skip) | passenger cannot start the engine |
| PassengerSync | `PatchSetCameraScripts` | `EnterVehicleController.SetCameraScripts` | - / postfix | passenger camera |
| PassengerSync | `PatchInputAxis` | `InputManager.GetAxis*` and similar | prefix (skip) | passenger input reads 0 |
| PassengerSync | `PatchInputButton` | `InputManager.GetButton*` and similar | prefix (skip) | passenger input reads false |
| PassengerSync | `PatchPickBody` | `PickBodyController.FixedUpdate` | prefix / finalizer | "Pick" counts only for the local driver |
| PurchaseSync | `PatchItemBuyCoroutines` | `CashRegisterGUI.BuyDelayed*` MoveNext | prefix / finalizer | context |
| PurchaseSync | `PatchSetPosition` | `CashRegisterGUI.SetPosition` | - / postfix | item spawn / push aside |
| PurchaseSync | `PatchTrailerCoroutines` | `CashRegisterGUI.SpawnTrailer` / `TrailerSpawner.SpawnTrailer` MoveNext | prefix / postfix / finalizer | trailer purchase |
| PurchaseSync | `PatchCarBuyObject` | `CashRegisterCarGUI.BuyObject` MoveNext | prefix / postfix / finalizer | vehicle purchase |
| PurchaseSync | `PatchDepotTeleport` | `Depot.Teleport` | prefix | context |
| PurchaseSync | `PatchPlaceTransport` | `DepotPlace.TransportGameObject` | prefix | context, reset moves |
| PurchaseSync | `PatchParcelReset` | `BankParcelGUI.ResetConfirm` | prefix / postfix / finalizer | parcel reset |
| PurchaseSync | `PatchBuyoutBuy` | `BuyoutRentedShopItemGUI.OnBuyButtonClicked` | prefix / postfix | rental buyout |
| PurchaseSync | `PatchBuyoutSell` | `BuyoutRentedShopItemGUI.OnSellButtonClicked` | prefix | rental return |
| PurchaseSync | `PatchDailyRentalReturn` | `Finance.ReturnMachine` | prefix (skip) | host's daily rental return reaches the clients |
| PurchaseSync | `PatchSellObjects` | `SellGUI.SellObjects` | prefix / postfix | sales |
| PurchaseSync | `PatchWorkersHire` | `WorkersToHire.Hire`, `LaptopWorkersHire.HireWorker` | prefix / postfix | hired workers |
| SaveTransfer | `QuickSavePatch` | `CheckpointManager.QuickSave` | prefix (skip) | redirect or skip saves |
| SaveTransfer | `RequestLoadPatch` | `CheckpointManager.RequestLoad` | prefix (skip) | refuse stale loads, leave on an own load |
| SaveTransfer | `OnLoadSaveDataPatch` | `CheckpointManager.OnLoadSaveData` | - / postfix | reason for a refused world |
| SaveTransfer | `ApplySaveDataPatch` | `CheckpointManager.ApplySaveDataToScene` | - / postfix | apply failure |
| TerrainSync | `PatchVoxelWrites` | `GoldTerrainDigger` voxel writers (`DigWithBox`, `TryMove`, `DigGold`...) | prefix | voxel diff marking |
| TerrainSync | `PatchCheckGold` | `GoldTerrainDigger.CheckGold` | - / postfix | drill points |
| TerrainSync | `PatchResetParcell` | `GoldTerrainDigger.ResetParcell` | prefix / finalizer | seeded, shared reset |
| TerrainSync | `PatchSetHeights` | `TerrainData.SetHeights` | prefix / postfix | terrain edits |
| TerrainSync | `PatchSetHeightsDelayLOD` | `TerrainData.SetHeightsDelayLOD` | prefix / postfix | terrain edits |
| TerrainSync | `PatchSetAlphamaps` | `TerrainData.SetAlphamaps` | prefix / postfix | terrain edits |
| TerrainSync | `PatchFreezeDirt` | `GoldTerrainDigger.FreezeDirt` | prefix / finalizer | the frost texture stays local |
| TutorialSync | `PatchEnterStep` | `TutorialRunner.EnterStep` | prefix / postfix | entry counting |
| WorldSync | `PatchSavedAdd` | `CheckPointManagerBase.Add` | - / postfix | world index |
| WorldSync | `PatchUse` | `UseAction.StartUsing` / `StopUsing` / `...Second` | prefix / postfix | switches and levers |
| WorldSync | `PatchExplosion` | `Explosion.PlayExplosionEffects` | - / postfix | explosions on every PC |
| WorldSync | `PatchPaintShop` | `PaintGUI.BuyConfirm` | prefix / finalizer | paint |
| WorldSync | `PatchPaintMe` | `MachinePainter.PaintMe` | - / postfix | paint |
| WorldSync | `PatchThunder` | `SeasonController.Thunder_OnFirsthunder` | prefix / finalizer | seeded lightning |
| WorldSync | `PatchBears` | `SeasonController.Penalties` | - / postfix | bears on the host only |

## 8. Ownership and authority

| What | Who is authoritative | How it moves |
|---|---|---|
| World, economy, clock, tutorial, fuel tanks, random events | host | |
| A machine | the PC whose player drives it | streams poses, parts and state; the others play back kinematically; on exit it keeps streaming while the body rolls on (coasting, up to 20 s), then a reliable final pose and state |
| A parked machine / trailer (nobody drives it) | host | once at rest, its pose is sent (repeated every 4 s for 30 s); clients snap their copy when it is more than 3 cm off and the local player is not acting on it |
| Machine processing (digging, dumping, wash plants on a machine) | `MachineSync.IsAuthority`: the driver, otherwise the host | |
| A trailer | the PC of the machine that tows it, otherwise the host (`IsTrailerAuthority`) | |
| Loose objects | an owner per object (`Owner`, `Epoch`) | claimed by grabbing, by local motion near the player, by a driver for towed / tied loads (rope hooks on its machine or on the trailer it tows, its pick rope). Never claimed by proximity or motion: loads tied to a machine another player drives, and objects whose owner drives a machine within 15 m. Ties break by epoch and then by the lower LocalId (`SyncLogic` ownership rules). When an owner leaves, the host takes over. |
| Truck bed cargo | the driver | |

## 9. Testing

- **Two instances on one PC:** start one with `--coop-host` and the other with `--coop-join 127.0.0.1 --coop-lowgfx`. The game folder needs `steam_appid.txt`.
- **Unattended in-game tests (TestProbe.cs, TestScenarios.cs; inert without the flags):** `--coop-load <slot>` loads that slot from the main menu. `--coop-probe`: the host walks a 4 m circle, both log `PROBE` lines. `--coop-test` (both instances): the host plays cash, tutorial step, purchase, grab/carry/drop, truck bed loading, a drive with exit while rolling, the same drive from the client, and a real `GoldTerrainDigger.DigWithBox` dig on a parcel; both log `TEST <tag> <utc> <values>` fingerprints every 0.25 s (tracked list shared through `%TEMP%\gmscoop_test_track.txt`). Compare the two logs to get delay, error and final state per scenario.
- **Logs:** `BepInEx/LogOutput.log` (and `.log.1` for the second instance). Budget: a 3-hour session must not add megabytes.
  - Each system logs one traffic line per minute.
  - Errors are rate limited per system and per message id.
  - Per-event lines that can repeat during play (ObjectSync attach / create / merge / stack op) are logged for the first 20 of each kind per session, then only counted in the minute line.
  - Status texts are logged once (a repeat is not logged); the client's transfer progress is logged at every 25 %.
  - Warnings in a path that can repeat every frame or every packet must log once or be rate limited.
  - The diagnostic dump is Ctrl+F12 (F12 alone is Steam's screenshot key).
- **Pure-logic harnesses:** earlier rounds kept harnesses for `SyncLogic.cs`, `SyncCore.cs` and `VehiclePlayback` in the review scratchpads. They compile those files unchanged. The `SyncLogic` harness also reports playback-rate surging per scenario, and a long-session test (game clocks at 0 / 3 / 8 h): with float stamps the shown acceleration p99 grows 15 -> 30 -> 54 m/s^2, with double stamps it stays at 14.7.
- **Build:**
  ```
  dotnet build -c Release -p:GameManaged=<game>\GoldMiningSimulator_Data\Managed
  ```
  If `GameDir` points at the install, the DLL is copied to `BepInEx/plugins`.

## 10. Release (friends package) and launcher

- `release\make_release.ps1` builds `release\out\GMS Co-op.exe`:
  1. checks that `GMSCoopin\Release
et472\GMSCoop.dll` exists and is newer than `Plugin.cs`;
  2. reads the plugin version from `Plugin.Version` in `Plugin.cs`;
  3. stages `toolsepinex.zip` (BepInEx 5) plus `BepInEx\plugins\GMSCoop.dll`;
  4. zips the stage as `Installer\payload.zip` with `/` separators;
  5. builds `Installer` with `-p:Version=<plugin version>`.
- `Installer\` is the launcher (WPF in code, no XAML, net48, borderless window, no admin prompt):
  - `Core.cs`: `GameInstall` (finds the game through the Steam registry keys, `libraryfolders.vdf` and `appmanifest_451340.acf`; install/uninstall rules as before: every path inside the game folder, an existing BepInEx 5 kept, BepInEx removed only with the marker `BepInEx\gmscoop_installed_bepinex.txt` and no other mods; "up to date" = the installed `GMSCoop.dll` has the same SHA-256 as the payload's; `steam_appid.txt` written so a direct exe launch keeps its arguments), `SteamArt` (hero/logo/header from `Steamppcache\librarycache`, old and new layouts; nothing shipped), `SaveSlots` (`%USERPROFILE%\AppData\LocalLow\CodeHorizon\GoldMiningSimulator\Saves\*`, skips `Coop_Host`, reads `Screenshot.jpg` and `SmallDescription.txt`), `Settings` (`%APPDATA%\GMSCoop\launcher.ini`), `LanBrowser` (listens for the mod's beacon on UDP 27778, pings hosts), `JoinTarget` (code parsing: 15-20 digits = Steam lobby, `CF-`/trycloudflare = Cloudflare, else address).
  - `MainWindow.cs` / `Ui.cs`: views Inicio / Crear partida / Unirse / Ajustes; auto install/update on start; game launch flags: host Steam `--coop-steam-host`, host LAN or Cloudflare `--coop-host`, plus `--coop-load <slot>`; join `+connect_lobby <id>`, `--coop-join <addr>` or `--coop-join auto`; `--coop-lowgfx` from Ajustes. `--preview <png> <view>` renders a view offscreen; `--selftest-cloudflare <txt>` runs a tunnel round trip through a local echo server.
  - `CloudflareBridge.cs`: Cloudflare quick tunnel. Host: `cloudflared tunnel --no-autoupdate --url http://127.0.0.1:<p>` (downloaded on demand from the official GitHub release into `%APPDATA%\GMSCoop`, killed with the launcher through a job object) in front of a hand-rolled RFC 6455 server (`HostBridge`) that pipes each WebSocket to the game on TCP 27777. It connects to the game only on the first game bytes, so connection checks never reach the game. Friend: `ClientBridge` listens on 127.0.0.1 and pipes each game connection to `wss://<sub>.trycloudflare.com` (ClientWebSocket). Binary frames carry the game byte stream; **text frames are reserved for a future signaling side-channel** (for example STUN/hole-punching address exchange) and are ignored today.
- Mod side of the launcher: `LanDiscovery.cs` (host beacon every 1 s to 255.255.255.255, every network's broadcast address and 127.0.0.1: `GMSCOOP1|protocol|version|name|players|port|joinable`; search for F10 without `HostIp` and `--coop-join auto`, 30 s timeout), `Plugin.JoinAddress` (`host`, `host:port`, `[ipv6]:port`, `auto`), `--coop-steam-host`, the Steam lobby code shown in the overlay and copied to the clipboard (`SteamLobby.OnCreated`).
- Version rule: the plugin version (`Plugin.cs`), `Net.Protocol`, README and this file must agree before a release. Bump `Net.Protocol` for any wire change; the launcher follows the plugin version automatically.

## 11. Direct link (`DirectLink.cs`)

`Net.StartLanHost` / `StartLanClient` wrap `LanTransport` in `HybridTransport`, which Net sees as an ordinary `ITransport` (`Name` stays "LAN"). Steam sessions are unchanged.

**Channels.** Each reliable message gets a channel from its message id (`HybridTransport.ChannelOf`): 0 control (Hello), 2 bulk (SaveChunk, CoopSession uploads (19), and SaveControl Preparing/Failed so a chunk never overtakes its Preparing), 1 gameplay (everything else, including Bye, so it never overtakes the ledger flush sent right before it, and CatchUp and the journal replays, which must stay in order with each other). Every channel has its own sequence numbers and is delivered in order on its own, whichever path carried each message; the receiver drops duplicates. Unreliable messages carry one sequence space with a 64-message replay window (duplicates and stale copies dropped).

**TCP path.** Tags on every packet: `0xE0` reliable `[u8 ch][u32 seq]`, `0xE1` unreliable `[u32 useq]`, `0xE3` control. An untagged packet comes from a build without this layer; the link then stays plain (so an old build still reads the version-mismatch Bye). The host sends a lane token on lane 0; the client opens lanes 1-3 (`[0xE5][u64 token][u8 lane]` as first frame). Lane 2 carries bulk, lanes 1 and 3 both carry every snapshot (latest-only queues: older frames are dropped when a lane backs up), lane 0 the rest. A lane that fails or closes only falls back to lane 0. Bulk messages sent on lane 2 are kept until the receiver's cumulative channel ack (`CtlChanAck`, every 0.25 s on lane 0); if lane 2 closes, the unacknowledged ones are re-sent on lane 0. A client that hears nothing for 5 s after its tagged Hello sends the Hello once untagged, so an old host answers with its version-mismatch Bye; untagged packets on a tagged link are ignored. Packets from a peer that left never re-create its link. A control ping on lane 0 every 2 s measures the tunnel RTT and keeps it alive.

**Direct setup.** The client (only when it joined a loopback address, the Cloudflare bridge, or a public IP) gathers candidates: its public mapping from three STUN servers, its LAN addresses and loopback, and sends OFFER on lane 0. The host answers with a link id, a random 16-byte secret, its candidates and the offer id (an answer to an older offer is ignored). Both send PUNCH datagrams (nonce + HMAC tag) to every candidate for up to 10 s; a valid PUNCH from an unlisted address is accepted too (peer-reflexive, symmetric NAT on the other side). A side sends on UDP once it gets a PUNCH_ACK for a nonce it sent; a PUNCH nonce is accepted once. Failure: retried after 20, 45, 90, 180, 300 s.

**Secure reliable UDP.** Datagram `"GC" [16][u32 link id][u32 packet number][AES-128-CTR frames][8-byte HMAC-SHA256 tag]`, keys derived from the secret; a 4096-packet window rejects replays; an authenticated packet from a new address is followed (NAT rebinding). Frames: STREAM (fragment of a channel message), UNREL, ACK (ranges), PING/PONG, PAD (MTU probe 1200 -> 1400: a probe carries only padding; fragments are always sized for 1200, a proven 1400 only packs more frames; 6 lost large packets within 2 s step back to 1200). Loss: 3 newer packets acknowledged, or 9/8 RTT behind the newest acknowledged one, or the RTO (RFC 6298, back-off). Lost fragments are queued once and go out in new packets. Congestion: slow start, additive increase; the window shrinks on a loss rate over 10 % or a growing RTT (queueing), not on random loss; sending is paced. Priority per packet: ACK, PONG, snapshots, then lost and new fragments of control, gameplay, bulk.

**Path choice and fallback.** Bulk goes on UDP while it is up; gameplay and snapshots too, unless the tunnel measures at least 20 % and 15 ms faster. UDP silent for 10 s (a peer's main thread can freeze for seconds while it loads the world), or 6 timeouts in a row: back to the TCP path, every message not acknowledged over UDP is re-sent there in order (the receiver drops what it already has), and the side tells the other one (`CtlDown`), which falls back too, so both always use the same path. After a link that worked, the client offers again after 3 s. Every change from direct to tunnel goes through `Fallback`. The UDP receive thread backs off on persistent socket errors. The overlay shows the path per peer ("Directo", "Cloudflare+", "LAN").

**Render delay.** `Net.AdaptRenderDelay` lowers the floor of `SyncClock` and `VehiclePlayback` to 60 ms when every peer is on a clean direct or LAN link (jitter under 10 ms, loss under 1 %), 100 ms otherwise; the clocks already adapt above the floor to each sender's gaps and jitter, and slew smoothly.

**Harness.** A console harness (outside the repo) compiles `TransportCore.cs` + `DirectLink.cs` with a simulated tunnel (fair-shared lanes, per-lane loss stalls) and a simulated UDP network (loss, jitter, duplicates, router queue, blackouts, port changes): ordering per channel, switchover and fallback with no loss or duplicate, throughput at 0/2/5 % loss, pose latency under bulk load, replay and forgery rejection, and real sockets with STUN.
