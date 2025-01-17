using System;
using System.Collections.Generic;

using log4net;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.Physics.Common;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// A generator profile for a Generator
    /// </summary>
    public class GeneratorProfile
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The biota with all the generator profile info
        /// </summary>
        public BiotaPropertiesGenerator Biota;

        /// <summary>
        /// A list of objects that have been spawned by this generator
        /// Mapping of object guid => registry node, which provides a bunch of detailed info about the spawn
        /// </summary>
        public readonly Dictionary<uint, WorldObjectInfo> Spawned = new Dictionary<uint, WorldObjectInfo>();

        /// <summary>
        /// The list of pending times awaiting respawning
        /// </summary>
        public readonly List<DateTime> SpawnQueue = new List<DateTime>();

        /// <summary>
        /// The list of pending times awaiting slot removal
        /// </summary>
        public readonly Queue<(DateTime time, uint objectGuid)> RemoveQueue = new Queue<(DateTime time, uint objectGuid)>();

        /// <summary>
        /// Returns TRUE if this profile is a placeholder object
        /// Placeholder objects are used for linkable generators,
        /// and are used as a template for the real items contained in the links.
        /// </summary>
        public bool IsPlaceholder { get => Biota.WeenieClassId == 3666; }

        /// <summary>
        /// The total # of active spawned objects + awaiting spawning
        /// </summary>
        public int CurrentCreate { get => Spawned.Count + SpawnQueue.Count; }

        /// <summary>
        /// Returns the MaxCreate for this generator profile
        /// If set to -1 in the database, use MaxCreate from generator
        /// </summary>
        public int MaxCreate { get => Biota.MaxCreate > -1 ? Biota.MaxCreate : Generator.MaxCreate; }

        /// <summary>
        /// Returns TRUE if the initial # of objects have been spawned
        /// </summary>
        public bool InitObjectsSpawned { get => CurrentCreate >= Biota.InitCreate; }

        /// <summary>
        /// Returns TRUE if the maximum # of objects have been spawned
        /// </summary>
        public bool MaxObjectsSpawned { get => CurrentCreate >= MaxCreate; }

        /// <summary>
        /// The delay for respawning objects
        /// </summary>
        public float Delay
        {
            get
            {
                if (Generator is Chest || Generator.RegenerationInterval == 0)
                    return 0;

                return Biota.Delay ?? Generator.GeneratorProfiles[0].Biota.Delay ?? 0.0f;
            }
        }

        /// <summary>
        /// The generator world object for this profile
        /// </summary>
        public WorldObject Generator;

        public RegenLocationType RegenLocationType => (RegenLocationType)Biota.WhereCreate;

        /// <summary>
        /// Constructs a new active generator profile
        /// from a biota generator
        /// </summary>
        public GeneratorProfile(WorldObject generator, BiotaPropertiesGenerator biota)
        {
            Generator = generator;
            Biota = biota;
        }

        /// <summary>
        /// Called every ~5 seconds for generator<para />
        /// Processes the RemoveQueue
        /// </summary>
        public void Maintenance_HeartBeat()
        {
            while (RemoveQueue.TryPeek(out var result) && result.time <= DateTime.UtcNow)
            {
                RemoveQueue.Dequeue();
                FreeSlot(result.objectGuid);
            }
        }

        /// <summary>
        /// Called every ~5 seconds for generator based on conditions<para />
        /// Processes the SpawnQueue
        /// </summary>
        public void Spawn_HeartBeat()
        {
            if (SpawnQueue.Count > 0)
                ProcessQueue();
        }

        /// <summary>
        /// Determines the spawn times for initial object spawning,
        /// and for respawning
        /// </summary>
        public DateTime GetSpawnTime()
        {
                return DateTime.UtcNow;
        }

        /// <summary>
        /// Enqueues 1 or multiple objects from this generator profile
        /// adds these items to the spawn queue
        /// </summary>
        public void Enqueue(int numObjects = 1, bool initialSpawn = true)
        {
            for (var i = 0; i < numObjects; i++)
            {
                /*if (MaxObjectsSpawned)
                {
                    log.Debug($"{_generator.Name}.Enqueue({numObjects}): max objects reached");
                    break;
                }*/
                SpawnQueue.Add(GetSpawnTime());
                if (initialSpawn)
                    Generator.CurrentCreate++;
            }
        }

        /// <summary>
        /// Spawns generator objects at the correct SpawnTime
        /// Called on heartbeat ticks every ~5 seconds
        /// </summary>
        public void ProcessQueue()
        {
            var index = 0;
            while (index < SpawnQueue.Count)
            {
                var queuedTime = SpawnQueue[index];

                if (queuedTime > DateTime.UtcNow)
                {
                    // not time to spawn yet
                    index++;
                    continue;
                }

                if ((RegenLocationType & RegenLocationType.Treasure) != 0)
                    RemoveTreasure();

                if (Spawned.Count < MaxCreate)
                {
                    var objects = Spawn();

                    if (objects != null)
                    {
                        foreach (var obj in objects)
                        {
                            var woi = new WorldObjectInfo(obj);

                            Spawned.Add(obj.Guid.Full, woi);
                        }
                    }
                    else
                    {
                        //_generator.CurrentCreate--;
                    }
                }
                else
                {
                    // this shouldn't happen (hopefully)
                    log.Debug($"GeneratorProfile: objects enqueued for {Generator.Name}, but MaxCreate({MaxCreate}) already reached!");
                }
                SpawnQueue.RemoveAt(index);
            }
        }

        /// <summary>
        /// Spawns an object from the generator queue
        /// for RNG treasure, can spawn multiple objects
        /// </summary>
        public List<WorldObject> Spawn()
        {
            var objects = new List<WorldObject>();

            if (RegenLocationType.HasFlag(RegenLocationType.Treasure))
            {
                objects = TreasureGenerator();
                if (objects.Count > 0)
                    Generator.GeneratedTreasureItem = true;
            }
            else
            {
                var wo = WorldObjectFactory.CreateNewWorldObject(Biota.WeenieClassId);
                if (wo == null)
                {
                    log.Debug($"{Generator.Name}.Spawn(): failed to create wcid {Biota.WeenieClassId}");
                    return null;
                }

                if (Biota.PaletteId.HasValue && Biota.PaletteId > 0)
                    wo.PaletteBaseId = Biota.PaletteId;

                if (Biota.Shade.HasValue && Biota.Shade > 0)
                    wo.Shade = Biota.Shade;

                if ((Biota.Shade.HasValue && Biota.Shade > 0) || (Biota.PaletteId.HasValue && Biota.PaletteId > 0))
                    wo.CalculateObjDesc(); // to update icon

                if (Biota.StackSize.HasValue && Biota.StackSize > 0)
                    wo.SetStackSize(Biota.StackSize);

                objects.Add(wo);
            }

            foreach (var obj in objects)
            {
                //log.Debug($"{_generator.Name}.Spawn({obj.Name})");

                obj.Generator = Generator;
                obj.GeneratorId = Generator.Guid.Full;

                if (RegenLocationType.HasFlag(RegenLocationType.Specific))
                    Spawn_Specific(obj);

                else if (RegenLocationType.HasFlag(RegenLocationType.Scatter))
                    Spawn_Scatter(obj);

                else if (RegenLocationType.HasFlag(RegenLocationType.Contain))
                    Spawn_Container(obj);

                else if (RegenLocationType.HasFlag(RegenLocationType.Shop))
                    Spawn_Shop(obj);

                else
                    Spawn_Default(obj);
            }
            return objects;
        }

        /// <summary>
        /// Spawns an object at a specific position
        /// </summary>
        public void Spawn_Specific(WorldObject obj)
        {
            // specific position
            if ((Biota.ObjCellId ?? 0) > 0)
                obj.Location = new ACE.Entity.Position(Biota.ObjCellId ?? 0, Biota.OriginX ?? 0, Biota.OriginY ?? 0, Biota.OriginZ ?? 0, Biota.AnglesX ?? 0, Biota.AnglesY ?? 0, Biota.AnglesZ ?? 0, Biota.AnglesW ?? 0);

            // offset from generator location
            else
                obj.Location = new ACE.Entity.Position(Generator.Location.Cell, Generator.Location.PositionX + Biota.OriginX ?? 0, Generator.Location.PositionY + Biota.OriginY ?? 0, Generator.Location.PositionZ + Biota.OriginZ ?? 0, Biota.AnglesX ?? 0, Biota.AnglesY ?? 0, Biota.AnglesZ ?? 0, Biota.AnglesW ?? 0);

            if (!VerifyLandblock(obj) || !VerifyWalkableSlope(obj)) return;

            obj.EnterWorld();
        }

        public void Spawn_Scatter(WorldObject obj)
        {
            float genRadius = (float)(Generator.GetProperty(PropertyFloat.GeneratorRadius) ?? 0f);
            obj.Location = new ACE.Entity.Position(Generator.Location);

            // we are going to delay this scatter logic until the physics engine,
            // where the remnants of this function are in the client (SetScatterPositionInternal)

            // this is due to each randomized position being required to go through the full InitialPlacement process, to verify success
            // if InitialPlacement fails, then we retry up to maxTries

            obj.ScatterPos = new SetPosition(new Physics.Common.Position(obj.Location), SetPositionFlags.RandomScatter, genRadius);

            obj.EnterWorld();

            obj.ScatterPos = null;
        }

        public void Spawn_Container(WorldObject obj)
        {
            var container = Generator as Container;

            if (container == null || !container.TryAddToInventory(obj))
                log.Debug($"{Generator.Name}.Spawn_Container({obj.Name}) - failed to add to container inventory");
        }

        public void Spawn_Shop(WorldObject obj)
        {
            // spawn item in vendor shop inventory
            var vendor = Generator as Vendor;

            if (vendor == null)
            {
                log.Debug($"{Generator.Name}.Spawn_Shop({obj.Name}) - generator is not a vendor type");
                return;
            }
            vendor.AddDefaultItem(obj);
        }

        public void Spawn_Default(WorldObject obj)
        {
            // default location handler?
            //log.Debug($"{_generator.Name}.Spawn_Default({obj.Name}): default handler for RegenLocationType {RegenLocationType}");

            obj.Location = new ACE.Entity.Position(Generator.Location);

            obj.EnterWorld();
        }

        public bool VerifyLandblock(WorldObject obj)
        {
            if (obj.Location == null || obj.Location.Landblock != Generator.Location.Landblock)
            {
                //log.Debug($"{_generator.Name}.VerifyLandblock({obj.Name}) - spawn location is invalid landblock");
                return false;
            }
            return true;
        }

        public bool VerifyWalkableSlope(WorldObject obj)
        {
            if (!obj.Location.Indoors && !obj.Location.IsWalkable())
            {
                //log.Debug($"{_generator.Name}.VerifyWalkableSlope({obj.Name}) - spawn location is unwalkable slope");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Generates a randomized treasure from LootGenerationFactory
        /// </summary>
        public List<WorldObject> TreasureGenerator()
        {
            // profile.WeenieClassId is not a weenieClassId,
            // it's a DeathTreasure or WieldedTreasure table DID
            // there is no overlap of DIDs between these 2 tables,
            // so they can be searched in any order..
            var deathTreasure = DatabaseManager.World.GetCachedDeathTreasure(Biota.WeenieClassId);
            if (deathTreasure != null)
            {
                // TODO: get randomly generated death treasure from LootGenerationFactory
                //log.Debug($"{_generator.Name}.TreasureGenerator(): found death treasure {Biota.WeenieClassId}");
                return LootGenerationFactory.CreateRandomLootObjects(deathTreasure);
            }
            else
            {
                var wieldedTreasure = DatabaseManager.World.GetCachedWieldedTreasure(Biota.WeenieClassId);
                if (wieldedTreasure != null)
                {
                    // TODO: get randomly generated wielded treasure from LootGenerationFactory
                    //log.Debug($"{_generator.Name}.TreasureGenerator(): found wielded treasure {Biota.WeenieClassId}");

                    // roll into the wielded treasure table
                    var table = new TreasureWieldedTable(wieldedTreasure);
                    return Generator.GenerateWieldedTreasureSets(table);
                }
                else
                {
                    log.Debug($"{Generator.Name}.TreasureGenerator(): couldn't find death treasure or wielded treasure for ID {Biota.WeenieClassId}");
                    return new List<WorldObject>();
                }
            }
        }

        /// <summary>
        /// Removes all of the objects from a container for this profile
        /// </summary>
        public void RemoveTreasure()
        {
            var container = Generator as Container;
            if (container == null)
            {
                log.Debug($"{Generator.Name}.RemoveTreasure(): container not found");
                return;
            }
            foreach (var spawned in Spawned.Keys)
            {
                var inventoryObjGuid = new ObjectGuid(spawned);
                if (!container.Inventory.TryGetValue(inventoryObjGuid, out var inventoryObj))
                {
                    log.Debug($"{Generator.Name}.RemoveTreasure(): couldn't find {inventoryObjGuid}");
                    continue;
                }
                container.TryRemoveFromInventory(inventoryObjGuid);
                inventoryObj.Destroy();
            }
            Spawned.Clear();
        }


        /// <summary>
        /// Callback system for objects notifying their generators of events,
        /// ie. item pickup
        /// </summary>
        public void NotifyGenerator(ObjectGuid target, RegenerationType eventType)
        {
            //log.Debug($"{_generator.Name}.NotifyGenerator({target:X8}, {eventType})");

            if (eventType == RegenerationType.PickUp && (RegenerationType)Biota.WhenCreate == RegenerationType.Destruction)
                eventType = RegenerationType.Destruction;

            // If WhenCreate is Undef, assume it means Destruction (bad data)
            if (eventType == RegenerationType.Destruction && (RegenerationType)Biota.WhenCreate == RegenerationType.Undef)
                Biota.WhenCreate = (uint)RegenerationType.Destruction;

            if (Biota.WhenCreate != (uint)eventType)
                return;

            Spawned.TryGetValue(target.Full, out var woi);

            if (woi == null) return;

            if (woi.WeenieClassId != Biota.WeenieClassId) return;

            RemoveQueue.Enqueue((DateTime.UtcNow.AddSeconds(Delay), woi.Guid.Full));
        }

        public void FreeSlot(uint objectGuid)
        {
            if (Spawned.Remove(objectGuid))
                Generator.CurrentCreate--;
        }
    }
}
