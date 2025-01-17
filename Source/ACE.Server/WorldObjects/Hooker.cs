using System;
using ACE.Database.Models.Shard;
using ACE.Database.Models.World;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.GameEvent.Events;

namespace ACE.Server.WorldObjects
{
    public class Hooker : WorldObject
    {
        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public Hooker(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public Hooker(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
            ActivationResponse |= ActivationResponse.Emote;
        }

        public override void ActOnUse(WorldObject activator)
        {
            // handled in base.OnActivate -> EmoteManager.OnUse()
        }

        public override ActivationResult CheckUseRequirements(WorldObject activator)
        {
            if (!(activator is Player player))
                return new ActivationResult(false);

            if (!OwnerId.HasValue || OwnerId.Value == 0)
                return new ActivationResult(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.ItemOnlyUsableOnHook, Name));

            var wo = player.CurrentLandblock.GetObject(OwnerId.Value);

            if (wo == null)
                return new ActivationResult(false);

            if (!(wo is Hook hook))
                return new ActivationResult(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.ItemOnlyUsableOnHook, Name));

            if (!hook.HouseOwner.HasValue || hook.HouseOwner.Value == 0 || (!hook.House.OpenStatus && !hook.House.HasPermission(player)))
                return new ActivationResult(new GameEventWeenieError(player.Session, WeenieError.YouAreNotPermittedToUseThatHook));

            var baseRequirements = base.CheckUseRequirements(activator);
            if (!baseRequirements.Success)
                return baseRequirements;

            return new ActivationResult(true);
        }
    }
}
