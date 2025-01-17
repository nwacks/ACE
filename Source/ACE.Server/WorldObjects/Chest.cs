using ACE.Database.Models.Shard;
using ACE.Database.Models.World;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameMessages.Messages;
using System.Collections.Generic;

namespace ACE.Server.WorldObjects
{
    public partial class Chest : Container, Lock
    {
        /// <summary>
        /// This is used for things like Mana Forge Chests
        /// </summary>
        public bool ChestRegenOnClose
        {
            get
            {
                if (ChestResetInterval <= 5)
                    return true;

                return GetProperty(PropertyBool.ChestRegenOnClose) ?? false;
            }
            set { if (!value) RemoveProperty(PropertyBool.ChestRegenOnClose); else SetProperty(PropertyBool.ChestRegenOnClose, value); }
        }

        /// <summary>
        /// This is the default setup for resetting chests
        /// </summary>
        public double ChestResetInterval
        {
            get
            {
                var chestResetInterval = ResetInterval ?? Default_ChestResetInterval;

                if (chestResetInterval < 15)
                    chestResetInterval = Default_ChestResetInterval;

                return chestResetInterval;
            }
        }

        public double Default_ChestResetInterval = 120;

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public Chest(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public Chest(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
            ContainerCapacity = ContainerCapacity ?? 10;
            ItemCapacity = ItemCapacity ?? 120;

            ActivationResponse |= ActivationResponse.Use;   // todo: fix broken data

            CurrentMotionState = motionClosed;              // do any chests default to open?

            if (IsLocked)
                DefaultLocked = true;

            if (DefaultLocked) // ignore regen interval, only regen on relock
                NextGeneratorRegenerationTime = double.MaxValue;
        }

        protected static readonly Motion motionOpen = new Motion(MotionStance.NonCombat, MotionCommand.On);
        protected static readonly Motion motionClosed = new Motion(MotionStance.NonCombat, MotionCommand.Off);

        public override ActivationResult CheckUseRequirements(WorldObject activator)
        {
            var baseRequirements = base.CheckUseRequirements(activator);
            if (!baseRequirements.Success)
                return baseRequirements;

            if (!(activator is Player player))
                return new ActivationResult(false);

            if (IsLocked)
            {
                EnqueueBroadcast(new GameMessageSound(Guid, Sound.OpenFailDueToLock, 1.0f));
                return new ActivationResult(false);
            }

            if (IsOpen)
            {
                // player has this chest open, close it
                if (Viewer == player.Guid.Full)
                    Close(player);

                // else another player has this chest open - send error message?
                else
                {
                    var currentViewer = CurrentLandblock.GetObject(Viewer) as Player;

                    // current viewer not found, close it
                    if (currentViewer == null)
                        Close(null);
                }

                return new ActivationResult(false);
            }

            // handle quest requirements
            if (Quest != null)
            {
                if (!player.QuestManager.HasQuest(Quest))
                    player.QuestManager.Update(Quest);
                else
                {
                    if (player.QuestManager.CanSolve(Quest))
                    {
                        player.QuestManager.Update(Quest);
                    }
                    else
                    {
                        player.QuestManager.HandleSolveError(Quest);
                        return new ActivationResult(false);
                    }
                }
            }

            return new ActivationResult(true);
        }

        /// <summary>
        /// This is raised by Player.HandleActionUseItem.<para />
        /// The item does not exist in the players possession.<para />
        /// If the item was outside of range, the player will have been commanded to move using DoMoveTo before ActOnUse is called.<para />
        /// When this is called, it should be assumed that the player is within range.
        /// </summary>
        public override void ActOnUse(WorldObject wo)
        {
            if (!(wo is Player player))
                return;

            // open chest
            Open(player);
        }

        public override void Open(Player player)
        {
            base.Open(player);

            if (!ResetMessagePending)
            {
                var actionChain = new ActionChain();
                actionChain.AddDelaySeconds(ChestResetInterval);
                actionChain.AddAction(this, Reset);
                actionChain.EnqueueChain();

                ResetMessagePending = true;
            }
        }

        public override void Close(Player player)
        {
            Close(player);
        }

        /// <summary>
        /// Called when a chest is closed, or walked away from
        /// </summary>
        public void Close(Player player, bool tryReset = true)
        {
            base.Close(player);

            if (ChestRegenOnClose && tryReset)
                Reset();
        }

        public override void Reset()
        {
            // TODO: if 'ResetInterval' style, do we want to ensure a minimum amount of time for the last viewer?

            var player = CurrentLandblock.GetObject(Viewer) as Player;

            if (IsOpen)
                Close(player, false);

            if (DefaultLocked && !IsLocked)
            {
                IsLocked = true;
                EnqueueBroadcast(new GameMessagePublicUpdatePropertyBool(this, PropertyBool.Locked, IsLocked));
            }

            if (IsGenerator)
            {
                ResetGenerator();
                if (InitCreate > 0)
                    Generator_Regeneration();
            }

            ResetMessagePending = false;
        }

        public override void ResetGenerator()
        {
            foreach (var generator in GeneratorProfiles)
            {
                var profileReset = false;

                foreach (var rNode in generator.Spawned.Values)
                {
                    var wo = rNode.TryGetWorldObject();

                    if (wo != null)
                    {
                        if (TryRemoveFromInventory(wo.Guid)) // only affect contained items.
                        {
                            wo.Destroy();
                        }

                        if (!(wo is Creature))
                            profileReset = true;
                    }
                }

                if (profileReset)
                {
                    generator.Spawned.Clear();
                    generator.SpawnQueue.Clear();
                    CurrentCreate--;
                }
            }

            if (GeneratedTreasureItem)
            {
                var items = new List<WorldObject>();
                foreach (var item in Inventory.Values)
                    items.Add(item);
                foreach (var item in items)
                {
                    if (TryRemoveFromInventory(item.Guid))
                        item.Destroy();
                }
                GeneratedTreasureItem = false;
            }
        }

        protected override float DoOnOpenMotionChanges()
        {
            return ExecuteMotion(motionOpen);
        }

        protected override float DoOnCloseMotionChanges()
        {
            return ExecuteMotion(motionClosed);
        }

        public string LockCode
        {
            get => GetProperty(PropertyString.LockCode);
            set { if (value == null) RemoveProperty(PropertyString.LockCode); else SetProperty(PropertyString.LockCode, value); }
        }

        /// <summary>
        /// Used for unlocking a chest via lockpick, so contains a skill check
        /// player.Skills[Skill.Lockpick].Current should be sent for the skill check
        /// </summary>
        public UnlockResults Unlock(uint unlockerGuid, uint playerLockpickSkillLvl, ref int difficulty)
        {
            var result = LockHelper.Unlock(this, playerLockpickSkillLvl, ref difficulty);

            if (result == UnlockResults.UnlockSuccess)
                LastUnlocker = unlockerGuid;

            return result;
        }

        /// <summary>
        /// Used for unlocking a chest via a key
        /// </summary>
        public UnlockResults Unlock(uint unlockerGuid, string keyCode)
        {
            var result = LockHelper.Unlock(this, keyCode);

            if (result == UnlockResults.UnlockSuccess)
                LastUnlocker = unlockerGuid;

            return result;
        }
    }
}
