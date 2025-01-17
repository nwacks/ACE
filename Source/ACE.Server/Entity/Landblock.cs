using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using log4net;

using ACE.Common;
using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Database.Models.World;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Physics.Common;
using ACE.Server.Network.GameMessages;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Entity
{
    /// <summary>
    /// the gist of a landblock is that, generally, everything on it publishes
    /// to and subscribes to everything else in the landblock.  x/y in an outdoor
    /// landblock goes from 0 to 192.  "indoor" (dungeon) landblocks have no
    /// functional limit as players can't freely roam in/out of them
    /// </summary>
    public class Landblock : IActor
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static float AdjacencyLoadRange { get; } = 96f;
        public static float OutdoorChatRange { get; } = 75f;
        public static float IndoorChatRange { get; } = 25f;
        public static float MaxXY { get; } = 192f;
        public static float MaxObjectRange { get; } = 192f;
        public static float MaxObjectGhostRange { get; } = 250f;


        public LandblockId Id { get; }

        /// <summary>
        /// Flag indicates if this landblock is permanently loaded (for example, towns on high-traffic servers)
        /// </summary>
        public bool Permaload = false;

        /// <summary>
        /// This must be true before a player enters a landblock.
        /// This prevents a player from possibly pasing through a door that hasn't spawned in yet, and other scenarios.
        /// </summary>
        public bool CreateWorldObjectsCompleted { get; private set; }

        private DateTime lastActiveTime;

        /// <summary>
        /// Dormant landblocks suppress Monster AI ticking and physics processing
        /// </summary>
        public bool IsDormant;

        private readonly Dictionary<ObjectGuid, WorldObject> worldObjects = new Dictionary<ObjectGuid, WorldObject>();
        private readonly Dictionary<ObjectGuid, WorldObject> pendingAdditions = new Dictionary<ObjectGuid, WorldObject>();
        private readonly List<ObjectGuid> pendingRemovals = new List<ObjectGuid>();

        // Cache used for Tick efficiency
        private readonly List<Player> players = new List<Player>();
        private readonly LinkedList<Creature> sortedCreaturesByNextTick = new LinkedList<Creature>();
        private readonly LinkedList<WorldObject> sortedWorldObjectsByNextHeartbeat = new LinkedList<WorldObject>();
        private readonly LinkedList<WorldObject> sortedGeneratorsByNextGeneratorUpdate = new LinkedList<WorldObject>();
        private readonly LinkedList<WorldObject> sortedGeneratorsByNextRegeneration = new LinkedList<WorldObject>();

        public List<Landblock> Adjacents = new List<Landblock>();

        private readonly ActionQueue actionQueue = new ActionQueue();

        /// <summary>
        /// Landblocks heartbeat every 5 seconds
        /// </summary>
        private static readonly TimeSpan heartbeatInterval = TimeSpan.FromSeconds(5);

        private DateTime lastHeartBeat = DateTime.MinValue;

        /// <summary>
        /// Landblock items will be saved to the database every 5 minutes
        /// </summary>
        private static readonly TimeSpan databaseSaveInterval = TimeSpan.FromMinutes(5);

        private DateTime lastDatabaseSave = DateTime.MinValue;

        /// <summary>
        /// Landblocks which have been inactive for this many seconds will be dormant
        /// </summary>
        private static readonly TimeSpan dormantInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Landblocks which have been inactive for this many seconds will be unloaded
        /// </summary>
        private static readonly TimeSpan unloadInterval = TimeSpan.FromMinutes(5);


        /// <summary>
        /// The clientlib backing store landblock
        /// Eventually these classes could be merged, but for now they are separate...
        /// </summary>
        public Physics.Common.Landblock _landblock { get; private set; }

        public CellLandblock CellLandblock { get; }
        public LandblockInfo LandblockInfo { get; }

        /// <summary>
        /// The landblock static meshes for
        /// collision detection and physics simulation
        /// </summary>
        public LandblockMesh LandblockMesh { get; private set; }
        public List<ModelMesh> LandObjects { get; private set; }
        public List<ModelMesh> Buildings { get; private set; }
        public List<ModelMesh> WeenieMeshes { get; private set; }
        public List<ModelMesh> Scenery { get; private set; }


        public readonly RateMonitor Monitor1h = new RateMonitor();
        private readonly TimeSpan last1hClearInteval = TimeSpan.FromHours(1);
        private DateTime last1hClear;


        public Landblock(LandblockId id)
        {
            //log.Debug($"Landblock({(id.Raw | 0xFFFF):X8})");

            Id = id;

            CellLandblock = DatManager.CellDat.ReadFromDat<CellLandblock>(Id.Raw >> 16 | 0xFFFF);
            LandblockInfo = DatManager.CellDat.ReadFromDat<LandblockInfo>((uint)Id.Landblock << 16 | 0xFFFE);

            lastActiveTime = DateTime.UtcNow;

            Task.Run(() =>
            {
                _landblock = LScape.get_landblock(Id.Raw);

                CreateWorldObjects();

                SpawnDynamicShardObjects();

                SpawnEncounters();
            });

            //LoadMeshes(objects);
        }

        /// <summary>
        /// Monster Locations, Generators<para />
        /// This will be called from a separate task from our constructor. Use thread safety when interacting with this landblock.
        /// </summary>
        private void CreateWorldObjects()
        {
            var objects = DatabaseManager.World.GetCachedInstancesByLandblock(Id.Landblock);
            var shardObjects = DatabaseManager.Shard.GetStaticObjectsByLandblock(Id.Landblock);
            var factoryObjects = WorldObjectFactory.CreateNewWorldObjects(objects, shardObjects);

            actionQueue.EnqueueAction(new ActionEventDelegate(() =>
            {
                // for mansion linking
                var houses = new List<House>();

                foreach (var fo in factoryObjects)
                {
                    WorldObject parent = null;
                    if (fo.WeenieType == WeenieType.House)
                    {
                        var house = fo as House;
                        Houses.Add(house);

                        if (fo.HouseType == HouseType.Mansion)
                        {
                            houses.Add(house);
                            house.LinkedHouses.Add(houses[0]);

                            if (houses.Count > 1)
                            {
                                houses[0].LinkedHouses.Add(house);
                                parent = houses[0];
                            }
                        }
                    }

                    AddWorldObject(fo);
                    fo.ActivateLinks(objects, shardObjects, parent);

                    if (fo.PhysicsObj != null)
                        fo.PhysicsObj.Order = 0;
                }

                CreateWorldObjectsCompleted = true;

                _landblock.SortObjects();
            }));
        }

        /// <summary>
        /// Corpses<para />
        /// This will be called from a separate task from our constructor. Use thread safety when interacting with this landblock.
        /// </summary>
        private void SpawnDynamicShardObjects()
        {
            var dynamics = DatabaseManager.Shard.GetDynamicObjectsByLandblock(Id.Landblock);
            var factoryShardObjects = WorldObjectFactory.CreateWorldObjects(dynamics);

            actionQueue.EnqueueAction(new ActionEventDelegate(() =>
            {
                foreach (var fso in factoryShardObjects)
                    AddWorldObject(fso);
            }));
        }

        /// <summary>
        /// Spawns the semi-randomized monsters scattered around the outdoors<para />
        /// This will be called from a separate task from our constructor. Use thread safety when interacting with this landblock.
        /// </summary>
        private void SpawnEncounters()
        {
            // get the encounter spawns for this landblock
            var encounters = DatabaseManager.World.GetCachedEncountersByLandblock(Id.Landblock);

            foreach (var encounter in encounters)
            {
                var wo = WorldObjectFactory.CreateNewWorldObject(encounter.WeenieClassId);

                if (wo == null) continue;

                var xPos = Math.Clamp(encounter.CellX * 24.0f, 0.5f, 191.5f);
                var yPos = Math.Clamp(encounter.CellY * 24.0f, 0.5f, 191.5f);

                var pos = new Physics.Common.Position();
                pos.ObjCellID = (uint)(Id.Landblock << 16) | 1;
                pos.Frame = new Physics.Animation.AFrame(new Vector3(xPos, yPos, 0), Quaternion.Identity);
                pos.adjust_to_outside();

                pos.Frame.Origin.Z = _landblock.GetZ(pos.Frame.Origin);

                wo.Location = new Position(pos.ObjCellID, pos.Frame.Origin, pos.Frame.Orientation);

                var sortCell = LScape.get_landcell(pos.ObjCellID) as SortCell;
                if (sortCell != null && sortCell.has_building())
                    continue;

                actionQueue.EnqueueAction(new ActionEventDelegate(() =>
                {
                    AddWorldObject(wo);
                }));
            }
        }

        /// <summary>
        /// Loads the meshes for the landblock<para />
        /// This isn't used by ACE, but we still retain it for the following reason:<para />
        /// its useful, concise, high level overview code for everything needed to load landblocks, all their objects, scenery, polygons
        /// without getting into all of the low level methods that acclient uses to do it
        /// </summary>
        private void LoadMeshes(List<LandblockInstance> objects)
        {
            LandblockMesh = new LandblockMesh(Id);
            LoadLandObjects();
            LoadBuildings();
            LoadWeenies(objects);
            LoadScenery();
        }

        /// <summary>
        /// Loads the meshes for the static landblock objects,
        /// also known as obstacles
        /// </summary>
        private void LoadLandObjects()
        {
            LandObjects = new List<ModelMesh>();

            foreach (var obj in LandblockInfo.Objects)
                LandObjects.Add(new ModelMesh(obj.Id, obj.Frame));
        }

        /// <summary>
        /// Loads the meshes for the buildings on the landblock
        /// </summary>
        private void LoadBuildings()
        {
            Buildings = new List<ModelMesh>();

            foreach (var obj in LandblockInfo.Buildings)
                Buildings.Add(new ModelMesh(obj.ModelId, obj.Frame));
        }

        /// <summary>
        /// Loads the meshes for the weenies on the landblock
        /// </summary>
        private void LoadWeenies(List<LandblockInstance> objects)
        {
            WeenieMeshes = new List<ModelMesh>();

            foreach (var obj in objects)
            {
                var weenie = DatabaseManager.World.GetCachedWeenie(obj.WeenieClassId);
                WeenieMeshes.Add(
                    new ModelMesh(weenie.GetProperty(PropertyDataId.Setup) ?? 0,
                    new DatLoader.Entity.Frame(new Position(obj.ObjCellId, obj.OriginX, obj.OriginY, obj.OriginZ, obj.AnglesX, obj.AnglesY, obj.AnglesZ, obj.AnglesW))));
            }
        }

        /// <summary>
        /// Loads the meshes for the scenery on the landblock
        /// </summary>
        private void LoadScenery()
        {
            Scenery = Entity.Scenery.Load(this);
        }

        public void Tick(double currentUnixTime)
        {
            Monitor1h.RegisterEventStart();

            ServerPerformanceMonitor.ResumeEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_RunActions);
            actionQueue.RunActions();
            ServerPerformanceMonitor.PauseEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_RunActions);

            ProcessPendingWorldObjectAdditionsAndRemovals();

            ServerPerformanceMonitor.ResumeEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_Player_Tick);
            foreach (var player in players)
                player.Player_Tick(currentUnixTime);
            ServerPerformanceMonitor.PauseEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_Player_Tick);

            // When a WorldObject Ticks, it can end up adding additional WorldObjects to this landblock
            if (!IsDormant)
            {
                ServerPerformanceMonitor.ResumeEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_Monster_Tick);
                while (sortedCreaturesByNextTick.Count > 0) // Monster_Tick()
                {
                    var first = sortedCreaturesByNextTick.First.Value;

                    // If they wanted to run before or at now
                    if (first.NextMonsterTickTime <= currentUnixTime)
                    {
                        sortedCreaturesByNextTick.RemoveFirst();
                        first.Monster_Tick(currentUnixTime);
                        sortedCreaturesByNextTick.AddLast(first); // All creatures tick at a fixed interval
                    }
                    else
                    {
                        break;
                    }
                }
                ServerPerformanceMonitor.PauseEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_Monster_Tick);
            }

            ServerPerformanceMonitor.ResumeEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_WorldObject_Heartbeat);
            while (sortedWorldObjectsByNextHeartbeat.Count > 0) // Heartbeat()
            {
                var first = sortedWorldObjectsByNextHeartbeat.First.Value;

                // If they wanted to run before or at now
                if (first.NextHeartbeatTime <= currentUnixTime)
                {
                    sortedWorldObjectsByNextHeartbeat.RemoveFirst();
                    first.Heartbeat(currentUnixTime);
                    InsertWorldObjectIntoSortedHeartbeatList(first); // WorldObjects can have heartbeats at different intervals
                }
                else
                {
                    break;
                }
            }
            ServerPerformanceMonitor.PauseEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_WorldObject_Heartbeat);

            ServerPerformanceMonitor.ResumeEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_GeneratorUpdate);
            while (sortedGeneratorsByNextGeneratorUpdate.Count > 0)
            {
                var first = sortedGeneratorsByNextGeneratorUpdate.First.Value;

                // If they wanted to run before or at now
                if (first.NextGeneratorUpdateTime <= currentUnixTime)
                {
                    sortedGeneratorsByNextGeneratorUpdate.RemoveFirst();
                    first.GeneratorUpdate(currentUnixTime);
                    //InsertWorldObjectIntoSortedGeneratorUpdateList(first);
                    sortedGeneratorsByNextGeneratorUpdate.AddLast(first);
                }
                else
                {
                    break;
                }
            }
            ServerPerformanceMonitor.PauseEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_GeneratorUpdate);

            ServerPerformanceMonitor.ResumeEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_GeneratorRegeneration);
            while (sortedGeneratorsByNextRegeneration.Count > 0) // GeneratorRegeneration()
            {
                var first = sortedGeneratorsByNextRegeneration.First.Value;

                //Console.WriteLine($"{first.Name}.Landblock_Tick_GeneratorRegeneration({currentUnixTime})");

                // If they wanted to run before or at now
                if (first.NextGeneratorRegenerationTime <= currentUnixTime)
                {
                    sortedGeneratorsByNextRegeneration.RemoveFirst();
                    first.GeneratorRegeneration(currentUnixTime);
                    InsertWorldObjectIntoSortedGeneratorRegenerationList(first); // Generators can have regnerations at different intervals
                }
                else
                {
                    break;
                }
            }
            ServerPerformanceMonitor.PauseEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_GeneratorRegeneration);

            // Heartbeat
            ServerPerformanceMonitor.ResumeEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_Heartbeat);
            if (lastHeartBeat + heartbeatInterval <= DateTime.UtcNow)
            {
                var thisHeartBeat = DateTime.UtcNow;

                ProcessPendingWorldObjectAdditionsAndRemovals();

                // Decay world objects
                if (lastHeartBeat != DateTime.MinValue)
                {
                    foreach (var wo in worldObjects.Values)
                    {
                        if (wo.IsDecayable())
                            wo.Decay(thisHeartBeat - lastHeartBeat);
                    }
                }

                if (!Permaload)
                {
                    if (lastActiveTime + dormantInterval < thisHeartBeat)
                        IsDormant = true;
                    if (lastActiveTime + unloadInterval < thisHeartBeat)
                        LandblockManager.AddToDestructionQueue(this);
                }

                //log.Info($"Landblock {Id.ToString()}.Tick({currentUnixTime}).Landblock_Tick_Heartbeat: thisHeartBeat: {thisHeartBeat.ToString()} | lastHeartBeat: {lastHeartBeat.ToString()} | worldObjects.Count: {worldObjects.Count()}");
                lastHeartBeat = thisHeartBeat;
            }
            ServerPerformanceMonitor.PauseEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_Heartbeat);

            // Database Save
            ServerPerformanceMonitor.ResumeEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_Database_Save);
            if (lastDatabaseSave + databaseSaveInterval <= DateTime.UtcNow)
            {
                ProcessPendingWorldObjectAdditionsAndRemovals();

                SaveDB();
                lastDatabaseSave = DateTime.UtcNow;
            }
            ServerPerformanceMonitor.PauseEvent(ServerPerformanceMonitor.MonitorType.Landblock_Tick_Database_Save);

            Monitor1h.RegisterEventEnd();

            if (DateTime.UtcNow - last1hClear >= last1hClearInteval)
            {
                Monitor1h.ClearEventHistory();
                last1hClear = DateTime.UtcNow;
            }
        }

        private void ProcessPendingWorldObjectAdditionsAndRemovals()
        {
            if (pendingAdditions.Count > 0)
            {
                foreach (var kvp in pendingAdditions)
                {
                    worldObjects[kvp.Key] = kvp.Value;

                    if (kvp.Value is Player player)
                        players.Add(player);
                    else if (kvp.Value is Creature creature)
                        sortedCreaturesByNextTick.AddLast(creature);

                    InsertWorldObjectIntoSortedHeartbeatList(kvp.Value);
                    InsertWorldObjectIntoSortedGeneratorUpdateList(kvp.Value);
                    InsertWorldObjectIntoSortedGeneratorRegenerationList(kvp.Value);
                }

                pendingAdditions.Clear();
            }

            if (pendingRemovals.Count > 0)
            {
                foreach (var objectGuid in pendingRemovals)
                {
                    if (worldObjects.Remove(objectGuid, out var wo))
                    {
                        if (wo is Player player)
                            players.Remove(player);
                        else if (wo is Creature creature)
                            sortedCreaturesByNextTick.Remove(creature);

                        sortedWorldObjectsByNextHeartbeat.Remove(wo);
                        sortedGeneratorsByNextGeneratorUpdate.Remove(wo);
                        sortedGeneratorsByNextRegeneration.Remove(wo);
                    }
                }

                pendingRemovals.Clear();
            }
        }

        private void InsertWorldObjectIntoSortedHeartbeatList(WorldObject worldObject)
        {
            // If you want to add checks to exclude certain object types from heartbeating, you would do it here
            if (worldObject.NextHeartbeatTime == double.MaxValue)
                return;

            if (sortedWorldObjectsByNextHeartbeat.Count == 0)
            {
                sortedWorldObjectsByNextHeartbeat.AddFirst(worldObject);
                return;
            }

            if (sortedWorldObjectsByNextHeartbeat.Last.Value.NextHeartbeatTime <= worldObject.NextHeartbeatTime)
            {
                sortedWorldObjectsByNextHeartbeat.AddLast(worldObject);
                return;
            }

            var currentNode = sortedWorldObjectsByNextHeartbeat.First;

            while (currentNode != null)
            {
                if (worldObject.NextHeartbeatTime <= currentNode.Value.NextHeartbeatTime)
                {
                    sortedWorldObjectsByNextHeartbeat.AddBefore(currentNode, worldObject);
                    return;
                }

                currentNode = currentNode.Next;
            }

            sortedWorldObjectsByNextHeartbeat.AddLast(worldObject); // This line really shouldn't be hit
        }

        private void InsertWorldObjectIntoSortedGeneratorUpdateList(WorldObject worldObject)
        {
            // If you want to add checks to exclude certain object types from heartbeating, you would do it here
            if (worldObject.NextGeneratorUpdateTime == double.MaxValue)
                return;

            if (sortedGeneratorsByNextGeneratorUpdate.Count == 0)
            {
                sortedGeneratorsByNextGeneratorUpdate.AddFirst(worldObject);
                return;
            }

            if (sortedGeneratorsByNextGeneratorUpdate.Last.Value.NextGeneratorUpdateTime <= worldObject.NextGeneratorUpdateTime)
            {
                sortedGeneratorsByNextGeneratorUpdate.AddLast(worldObject);
                return;
            }

            var currentNode = sortedGeneratorsByNextGeneratorUpdate.First;

            while (currentNode != null)
            {
                if (worldObject.NextGeneratorUpdateTime <= currentNode.Value.NextGeneratorUpdateTime)
                {
                    sortedGeneratorsByNextGeneratorUpdate.AddBefore(currentNode, worldObject);
                    return;
                }

                currentNode = currentNode.Next;
            }

            sortedGeneratorsByNextGeneratorUpdate.AddLast(worldObject); // This line really shouldn't be hit
        }

        private void InsertWorldObjectIntoSortedGeneratorRegenerationList(WorldObject worldObject)
        {
            // If you want to add checks to exclude certain object types from heartbeating, you would do it here
            if (worldObject.NextGeneratorRegenerationTime == double.MaxValue)
                return;

            if (sortedGeneratorsByNextRegeneration.Count == 0)
            {
                sortedGeneratorsByNextRegeneration.AddFirst(worldObject);
                return;
            }

            if (sortedGeneratorsByNextRegeneration.Last.Value.NextGeneratorRegenerationTime <= worldObject.NextGeneratorRegenerationTime)
            {
                sortedGeneratorsByNextRegeneration.AddLast(worldObject);
                return;
            }

            var currentNode = sortedGeneratorsByNextRegeneration.First;

            while (currentNode != null)
            {
                if (worldObject.NextGeneratorRegenerationTime <= currentNode.Value.NextGeneratorRegenerationTime)
                {
                    sortedGeneratorsByNextRegeneration.AddBefore(currentNode, worldObject);
                    return;
                }

                currentNode = currentNode.Next;
            }

            sortedGeneratorsByNextRegeneration.AddLast(worldObject); // This line really shouldn't be hit
        }

        public void ResortWorldObjectIntoSortedGeneratorRegenerationList(WorldObject worldObject)
        {
            if (sortedGeneratorsByNextRegeneration.Contains(worldObject))
            {
                sortedGeneratorsByNextRegeneration.Remove(worldObject);
                InsertWorldObjectIntoSortedGeneratorRegenerationList(worldObject);
            }
        }

        public void EnqueueAction(IAction action)
        {
            actionQueue.EnqueueAction(action);
        }

        private void AddPlayerTracking(List<WorldObject> wolist, Player player)
        {
            foreach (var wo in wolist)
                player.AddTrackedObject(wo);
        }

        /// <summary>
        /// This will fail if the wo doesn't have a valid location.
        /// </summary>
        public bool AddWorldObject(WorldObject wo)
        {
            if (wo.Location == null)
            {
                log.DebugFormat("Landblock 0x{0} failed to add 0x{1:X8} {2}. Invalid Location", Id, wo.Biota.Id, wo.Name);
                return false;
            }

            return AddWorldObjectInternal(wo);
        }

        public void AddWorldObjectForPhysics(WorldObject wo)
        {
            AddWorldObjectInternal(wo);
        }

        private bool AddWorldObjectInternal(WorldObject wo)
        {
            wo.CurrentLandblock = this;

            if (wo.PhysicsObj == null)
                wo.InitPhysicsObj();

            if (wo.PhysicsObj.CurCell == null)
            {
                var success = wo.AddPhysicsObj();
                if (!success)
                {
                    wo.CurrentLandblock = null;
                    if (wo.Generator != null)
                        log.Debug($"AddWorldObjectInternal: couldn't spawn 0x{wo.Guid}:{wo.Name} from generator {wo.Generator.WeenieClassId} - 0x{wo.Generator.Guid}:{wo.Generator.Name}");
                    else
                        log.Warn($"AddWorldObjectInternal: couldn't spawn 0x{wo.Guid}:{wo.Name}");
                    return false;
                }
            }

            if (!worldObjects.ContainsKey(wo.Guid))
                pendingAdditions[wo.Guid] = wo;
            else
                pendingRemovals.Remove(wo.Guid);

            // if adding a player to this landblock,
            // tell them about other nearby objects
            if (wo is Player || wo is CombatPet)
            {
                var newlyVisible = wo.PhysicsObj.handle_visible_cells();
                wo.PhysicsObj.enqueue_objs(newlyVisible);
            }

            // broadcast to nearby players
            wo.NotifyPlayers();

            return true;
        }

        public void RemoveWorldObject(ObjectGuid objectId, bool adjacencyMove = false, bool fromPickup = false)
        {
            RemoveWorldObjectInternal(objectId, adjacencyMove, fromPickup);
        }

        /// <summary>
        /// Should only be called by physics/relocation engines -- not from player
        /// </summary>
        /// <param name="objectId">The object ID to be removed from the current landblock</param>
        /// <param name="adjacencyMove">Flag indicates if object is moving to an adjacent landblock</param>
        public void RemoveWorldObjectForPhysics(ObjectGuid objectId, bool adjacencyMove = false)
        {
            RemoveWorldObjectInternal(objectId, adjacencyMove);
        }

        private void RemoveWorldObjectInternal(ObjectGuid objectId, bool adjacencyMove = false, bool fromPickup = false)
        {
            if (worldObjects.TryGetValue(objectId, out var wo))
                pendingRemovals.Add(objectId);
            else if (!pendingAdditions.Remove(objectId, out wo))
            {
                log.Warn($"RemoveWorldObjectInternal: Couldn't find {objectId.Full:X8}");
                return;
            }

            wo.CurrentLandblock = null;

            // Weenies can come with a default of 0 (Instant Rot) or -1 (Never Rot). If they still have that value, we want to retain it.
            // We also want to make sure fromPickup is true so that we're not clearing out TimeToRot on server shutdown (unloads all landblocks and removed all objects).
            if (fromPickup && wo.TimeToRot.HasValue && wo.TimeToRot != 0 && wo.TimeToRot != -1)
                wo.TimeToRot = null;

            if (!adjacencyMove)
            {
                // really remove it - send message to client to remove object
                wo.EnqueueActionBroadcast(p => p.RemoveTrackedObject(wo, fromPickup));

                wo.PhysicsObj.DestroyObject();
            }
        }

        /// <summary>
        /// Check to see if we are close enough to interact.   Adds a fudge factor of 1.5f
        /// </summary>
        public bool WithinUseRadius(Player player, ObjectGuid targetGuid, out bool validTargetGuid, float? useRadius = null)
        {
            var target = GetObject(targetGuid);

            validTargetGuid = target != null;

            if (target != null)
                return player.IsWithinUseRadiusOf(target, useRadius);

            return false;
        }

        /// <summary>
        /// Returns landblock objects with physics initialized
        /// </summary>
        public ICollection<WorldObject> GetWorldObjectsForPhysicsHandling()
        {
            // If a missile is destroyed when it runs it's UpdateObjectPhysics(), it will remove itself from the landblock, thus, modifying the worldObjects collection.

            ProcessPendingWorldObjectAdditionsAndRemovals();

            return worldObjects.Values;
        }

        public List<WorldObject> GetAllWorldObjectsForDiagnostics()
        {
            // We do not ProcessPending here, and we return ToList() to avoid cross-thread issues.
            // This can happen if we "loadalllandblocks" and do a "serverstatus".
            return worldObjects.Values.ToList();
        }

        public WorldObject GetObject(uint objectId)
        {
            return GetObject(new ObjectGuid(objectId));
        }

        /// <summary>
        /// This will return null if the object was not found in the current or adjacent landblocks.
        /// </summary>
        public WorldObject GetObject(ObjectGuid guid)
        {
            if (pendingRemovals.Contains(guid))
                return null;

            if (worldObjects.TryGetValue(guid, out var worldObject) || pendingAdditions.TryGetValue(guid, out worldObject))
                return worldObject;

            foreach (Landblock lb in Adjacents)
            {
                if (lb != null && !lb.pendingRemovals.Contains(guid) && (lb.worldObjects.TryGetValue(guid, out worldObject) || lb.pendingAdditions.TryGetValue(guid, out worldObject)))
                    return worldObject;
            }

            return null;
        }

        public WorldObject GetWieldedObject(uint objectGuid, bool searchAdjacents = true)
        {
            return GetWieldedObject(new ObjectGuid(objectGuid), searchAdjacents); // todo fix
        }

        /// <summary>
        /// Searches this landblock (and possibly adjacents) for an ObjectGuid wielded by a creature
        /// </summary>
        public WorldObject GetWieldedObject(ObjectGuid guid, bool searchAdjacents = true)
        {
            // search creature wielded items in current landblock
            var creatures = worldObjects.Values.OfType<Creature>();
            foreach (var creature in creatures)
            {
                var wieldedItem = creature.GetEquippedItem(guid);
                if (wieldedItem != null)
                {
                    if ((wieldedItem.CurrentWieldedLocation & EquipMask.Selectable) != 0)
                        return wieldedItem;

                    return null;
                }
            }

            // try searching adjacent landblocks if not found
            if (searchAdjacents)
            {
                foreach (var adjacent in Adjacents)
                {
                    if (adjacent == null) continue;

                    var wieldedItem = adjacent.GetWieldedObject(guid, false);
                    if (wieldedItem != null)
                        return wieldedItem;
                }
            }
            return null;
        }

        public void ResendObjectsInRange(WorldObject wo)
        {
            wo.PhysicsObj.ObjMaint.RemoveAllObjects();

            var visibleObjs = wo.PhysicsObj.handle_visible_cells();
            wo.PhysicsObj.enqueue_objs(visibleObjs);
        }

        /// <summary>
        /// Sets a landblock to active state, with the current time as the LastActiveTime
        /// </summary>
        /// <param name="isAdjacent">Public calls to this function should always set isAdjacent to false</param>
        public void SetActive(bool isAdjacent = false)
        {
            lastActiveTime = DateTime.UtcNow;
            IsDormant = false;

            if (isAdjacent || _landblock == null || _landblock.IsDungeon) return;

            // for outdoor landblocks, recursively call 1 iteration to set adjacents to active
            foreach (var landblock in Adjacents)
            {
                if (landblock != null)
                    landblock.SetActive(true);
            }
        }

        /// <summary>
        /// Handles the cleanup process for a landblock
        /// This method is called by LandblockManager
        /// </summary>
        public void Unload()
        {
            var landblockID = Id.Raw | 0xFFFF;

            //log.Debug($"Landblock.Unload({landblockID:X8})");

            ProcessPendingWorldObjectAdditionsAndRemovals();

            SaveDB();

            // remove all objects
            foreach (var wo in worldObjects.ToList())
            {
                if (!wo.Value.BiotaOriginatedFromOrHasBeenSavedToDatabase())
                    wo.Value.Destroy(false);
                else
                    RemoveWorldObjectInternal(wo.Key);
            }

            ProcessPendingWorldObjectAdditionsAndRemovals();

            actionQueue.Clear();

            // remove physics landblock
            LScape.unload_landblock(landblockID);
        }

        private void SaveDB()
        {
            var biotas = new Collection<(Biota biota, ReaderWriterLockSlim rwLock)>();

            foreach (var wo in worldObjects.Values)
            {
                if (wo.IsStaticThatShouldPersistToShard() || wo.IsDynamicThatShouldPersistToShard())
                    AddWorldObjectToBiotasSaveCollection(wo, biotas);
            }

            DatabaseManager.Shard.SaveBiotasInParallel(biotas, result => { });
        }

        private void AddWorldObjectToBiotasSaveCollection(WorldObject wo, Collection<(Biota biota, ReaderWriterLockSlim rwLock)> biotas)
        {
            if (wo.ChangesDetected)
            {
                wo.SaveBiotaToDatabase(false);
                biotas.Add((wo.Biota, wo.BiotaDatabaseLock));
            }

            if (wo is Container container)
            {
                foreach (var item in container.Inventory.Values)
                    AddWorldObjectToBiotasSaveCollection(item, biotas);
            }
        }

        /// <summary>
        /// This is only used for very specific instances, such as broadcasting player deaths to the destination lifestone block
        /// This is a rarely used method to broadcast network messages to all of the players within a landblock,
        /// and possibly the adjacent landblocks.
        /// </summary>
        public void EnqueueBroadcast(ICollection<Player> excludeList, bool adjacents, params GameMessage[] msgs)
        {
            var players = worldObjects.Values.OfType<Player>();

            // for landblock death broadcasts:
            // exclude players that have already been broadcast to within range of the death
            if (excludeList != null)
                players = players.Except(excludeList);

            // broadcast messages to player in this landblock
            foreach (var player in players)
                player.Session.Network.EnqueueSend(msgs);

            // if applicable, iterate into adjacent landblocks
            if (adjacents)
            {
                foreach (var adjacent in this.Adjacents.Where(adj => adj != null))
                    adjacent.EnqueueBroadcast(excludeList, false, msgs);
            }
        }

        private bool? isDungeon;

        /// <summary>
        /// Returns TRUE if this landblock is a dungeon
        /// </summary>
        public bool IsDungeon
        {
            get
            {
                // return cached value
                if (isDungeon != null)
                    return isDungeon.Value;

                // a dungeon landblock is determined by:
                // - all heights being 0
                // - having at least 1 EnvCell (0x100+)
                // - contains no buildings
                foreach (var height in CellLandblock.Height)
                {
                    if (height != 0)
                    {
                        isDungeon = false;
                        return isDungeon.Value;
                    }
                }
                isDungeon = LandblockInfo != null && LandblockInfo.NumCells > 0 && LandblockInfo.Buildings != null && LandblockInfo.Buildings.Count == 0;
                return isDungeon.Value;
            }
        }

        private bool? isHouseDungeon;

        public bool IsHouseDungeon
        {
            get
            {
                // return cached value
                if (isHouseDungeon != null)
                    return isHouseDungeon.Value;

                isHouseDungeon = IsDungeon ? DatabaseManager.World.GetCachedHousePortalsByLandblock(Id.Landblock).Count > 0 : false;

                return isHouseDungeon.Value;
            }
        }

        public List<House> Houses = new List<House>();
    }
}
