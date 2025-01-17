using System;
using System.Linq;

using ACE.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class WorldObject
    {
        /// <summary>
        /// The default number of seconds for a object on a landblock to disappear<para />
        /// Current default is 5 minutes
        /// </summary>
        protected TimeSpan DefaultTimeToRot { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// A decayable object is one that, when it exists on a landblock, would decay (rot) over time.<para />
        /// When it rots, it would be destroyed, and removed from the landblock.<para />
        /// In most cases, these should be player dropped items or corpses. It can also be a missile or spell projectile.<para />
        /// Items that have a TimeToRot value of -1 will return false.<para />
        /// Items that have a BaseDescriptionFlags with ObjectDescriptionFlag.Stuck set will return false.<para />
        /// Generators and items still linked to a generator will return false.
        /// </summary>
        public bool IsDecayable()
        {
            if (!Guid.IsDynamic())
                return false;

            if (TimeToRot.HasValue)
                return TimeToRot != -1; // -1 = Never Rot

            // Don't rot generators, and items that were generated by a generator
            // If the item was generated by a generator and then picked up by a player, the wo.Generator property would be set to null.
            if (IsGenerator || Generator != null)
                return false;

            return true;
        }

        private bool decayCompleted;

        public void Decay(TimeSpan elapsed)
        {
            // http://asheron.wikia.com/wiki/Item_Decay

            if (decayCompleted)
                return;

            var previousTTR = TimeToRot;

            if (!TimeToRot.HasValue)
            {
                TimeToRot = DefaultTimeToRot.TotalSeconds;

                if (this is Corpse && Level.HasValue)
                    log.Info($"{Name} (0x{Guid.ToString()}).Decay: TimeToRot had no value, set to {TimeToRot}");

                return;
            }

            var corpse = this as Corpse;

            if (corpse != null)
            {
                if (!corpse.InventoryLoaded)
                    return;

                if (corpse.Inventory.Count == 0 && TimeToRot.Value > Corpse.EmptyDecayTime)
                {
                    TimeToRot = Corpse.EmptyDecayTime;
                    if (Level.HasValue && PropertyManager.GetBool("corpse_decay_tick_logging").Item)
                        log.Info($"{corpse.Name} (0x{corpse.Guid.ToString()}).Decay({elapsed.ToString()}): InventoryLoaded = {corpse.InventoryLoaded} | Inventory.Count = {corpse.Inventory.Count} | previous TimeToRot: {previousTTR} | current TimeToRot: {TimeToRot}");
                    return;
                }
            }

            if (TimeToRot > 0)
            {
                TimeToRot -= elapsed.TotalSeconds;

                if (this is Corpse && Level.HasValue && PropertyManager.GetBool("corpse_decay_tick_logging").Item)
                    log.Info($"{corpse.Name} (0x{corpse.Guid.ToString()}).Decay({elapsed.ToString()}): previous TimeToRot: {previousTTR} | current TimeToRot: {TimeToRot}");

                // Is there still time left?
                if (TimeToRot > 0)
                    return;

                TimeToRot = -2; // We force it to -2 to make sure it doesn't end up at 0 or -1. 0 indicates instant rot. -1 indicates no rot. 0 and -1 can be found in weenie defaults

                if (this is Corpse && Level.HasValue && PropertyManager.GetBool("corpse_decay_tick_logging").Item)
                    log.Info($"{corpse.Name} (0x{corpse.Guid.ToString()}).Decay({elapsed.ToString()}): previous TimeToRot: {previousTTR} | current TimeToRot: {TimeToRot}");
            }

            if (this is Container container && container.IsOpen)
            {
                // If you wanted to add a grace period to the container to give Player B more time to open it after Player A closes it, it would go here.

                return;
            }

            // Time to rot has elapsed, time to disappear...
            decayCompleted = true;

            // If this is a player corpse, puke out the corpses contents onto the landblock
            if (corpse != null && !corpse.IsMonster)
            {
                var inventoryGUIDs = corpse.Inventory.Keys.ToList();

                var pukedItems = "";

                foreach (var guid in inventoryGUIDs)
                {
                    if (corpse.TryRemoveFromInventory(guid, out var item))
                    {
                        item.Location = new Position(corpse.Location);
                        item.Placement = ACE.Entity.Enum.Placement.Resting; // This is needed to make items lay flat on the ground.
                        CurrentLandblock.AddWorldObject(item);
                        item.SaveBiotaToDatabase();
                        pukedItems += $"{item.Name} (0x{item.Guid.Full.ToString("X8")}), ";
                    }
                }

                if (pukedItems.EndsWith(", "))
                    pukedItems = pukedItems.Substring(0, pukedItems.Length - 2);

                log.Info($"{corpse.Name} (0x{corpse.Guid.ToString()}) at {corpse.Location.ToLOCString()} has decayed{((pukedItems == "") ? "" : $" and placed the following items on the landblock: {pukedItems}")}.");
            }

            if (corpse != null)
            {
                EnqueueBroadcast(new GameMessageScript(Guid, ACE.Entity.Enum.PlayScript.Destroy));

                var actionChain = new ActionChain();
                actionChain.AddDelaySeconds(1.0f);
                actionChain.AddAction(this, () => Destroy());
                actionChain.EnqueueChain();
            }
            else
                Destroy();
        }
    }
}
