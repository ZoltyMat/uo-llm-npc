using System;
using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom.LLMNpc
{
    // P14: the genie rule. The Overseer can be moved — rarely — to grant a
    // mortal a weapon, but every gift of the realm is a BARGAIN: a real power
    // always paired with a real flaw. The LLM only ever decides THAT a gift
    // happens (via the [do:gift] tag, itself prompt-discouraged and
    // cooldown-gated); everything about the item — type, power, flaw, stats —
    // is rolled deterministically here. A sweet-talked model cannot mint an
    // unflawed weapon, because no unflawed weapon exists in the table.
    public static class GenieGifts
    {
        // A gift is a rare event: shard-wide spacing plus a long per-player
        // cooldown, so "ask three overseers in a row" gets one bargain at most.
        private static readonly TimeSpan GiftGlobalCooldown = TimeSpan.FromMinutes(30.0);
        private static readonly TimeSpan GiftPlayerCooldown = TimeSpan.FromHours(4.0);

        private static DateTime m_NextGiftUtc = DateTime.MinValue;
        private static readonly Dictionary<int, DateTime> m_NextPlayerGiftUtc =
            new Dictionary<int, DateTime>();

        // Grants a bargain-weapon to the player if the cooldowns allow. Returns
        // true when an item was actually given; false means the overseer should
        // play the moment off (the caller emotes a refusal).
        public static bool TryGrant(Mobile overseer, Mobile player)
        {
            if (overseer == null || player == null || player.Deleted || !player.Alive)
                return false;

            DateTime now = DateTime.UtcNow;

            if (now < m_NextGiftUtc)
                return false;

            DateTime nextForPlayer;
            if (m_NextPlayerGiftUtc.TryGetValue(player.Serial.Value, out nextForPlayer) &&
                now < nextForPlayer)
                return false;

            m_NextGiftUtc = now.Add(GiftGlobalCooldown);
            m_NextPlayerGiftUtc[player.Serial.Value] = now.Add(GiftPlayerCooldown);

            BaseWeapon gift = Roll();

            string power, flaw;
            ApplyBargain(gift, out power, out flaw);

            if (player.Backpack != null)
                player.Backpack.DropItem(gift);
            else
                gift.MoveToWorld(player.Location, player.Map);

            player.FixedParticles(0x375A, 10, 15, 5037, EffectLayer.Waist);
            player.PlaySound(0x1F7);

            overseer.Emote("*a shimmer passes between the Overseer's hands and " +
                (string.IsNullOrEmpty(player.Name) ? "the mortal" : player.Name) + "'s*");

            LLMClient.Log("GIFT overseer=" + overseer.Serial.Value + " player=" + player.Serial.Value +
                " item=" + gift.GetType().Name + " power=" + power + " flaw=" + flaw);

            return true;
        }

        private static BaseWeapon Roll()
        {
            switch (Utility.Random(3))
            {
                case 0: return new GenieKatana();
                case 1: return new GenieBroadsword();
                default: return new GenieWarAxe();
            }
        }

        // One power and one flaw, always both. Self-wounding is carried as a
        // flag on the Genie* subclass (its OnHit bites the wielder); the other
        // flaws ride ordinary item properties.
        public static void ApplyBargain(BaseWeapon w, out string power, out string flaw)
        {
            w.Name = "the Overseer's bargain";
            w.Hue = 0x481; // the Overseer's otherworldly violet

            switch (Utility.Random(4))
            {
                case 0:
                    w.Attributes.WeaponDamage = 50;
                    power = "strikes half again as hard";
                    break;
                case 1:
                    w.Attributes.WeaponSpeed = 30;
                    w.Attributes.AttackChance = 10;
                    power = "swings swift and sure";
                    break;
                case 2:
                    w.WeaponAttributes.HitLightning = 40;
                    w.Attributes.WeaponDamage = 20;
                    power = "calls lightning down on those it strikes";
                    break;
                default:
                    w.WeaponAttributes.HitLeechHits = 40;
                    w.Attributes.WeaponDamage = 20;
                    power = "drinks the life of its victims";
                    break;
            }

            IGenieWeapon genie = w as IGenieWeapon;

            switch (Utility.Random(3))
            {
                case 0:
                    if (genie != null)
                        genie.SelfWound = true;
                    flaw = "bites the hand that wields it";
                    break;
                case 1:
                    w.Attributes.RegenHits = -5;
                    flaw = "sips its bearer's life while held";
                    break;
                default:
                    w.LootType = LootType.Cursed;
                    w.Attributes.Luck = -200;
                    flaw = "cannot be kept from death's tithe, and luck sours around it";
                    break;
            }
        }

        // The self-wound flaw: a small bite back at the wielder on every landed
        // blow. Called by each Genie* subclass from its OnHit override.
        public static void OnHitFlaw(BaseWeapon w, Mobile attacker)
        {
            IGenieWeapon genie = w as IGenieWeapon;

            if (genie == null || !genie.SelfWound)
                return;

            if (attacker == null || attacker.Deleted || !attacker.Alive)
                return;

            attacker.Damage(Utility.RandomMinMax(2, 5), attacker);
        }
    }

    // Marker carried by every genie weapon type so the shared bargain/flaw
    // logic can reach the self-wound flag without per-type casts.
    public interface IGenieWeapon
    {
        bool SelfWound { get; set; }
    }

    public class GenieKatana : Katana, IGenieWeapon
    {
        private bool m_SelfWound;
        public bool SelfWound { get { return m_SelfWound; } set { m_SelfWound = value; } }

        [Constructable]
        public GenieKatana()
        {
        }

        public GenieKatana(Serial serial)
            : base(serial)
        {
        }

        public override void OnHit(Mobile attacker, IDamageable damageable, double damageBonus)
        {
            base.OnHit(attacker, damageable, damageBonus);
            GenieGifts.OnHitFlaw(this, attacker);
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write((int)0);
            writer.Write(m_SelfWound);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            reader.ReadInt();
            m_SelfWound = reader.ReadBool();
        }
    }

    public class GenieBroadsword : Broadsword, IGenieWeapon
    {
        private bool m_SelfWound;
        public bool SelfWound { get { return m_SelfWound; } set { m_SelfWound = value; } }

        [Constructable]
        public GenieBroadsword()
        {
        }

        public GenieBroadsword(Serial serial)
            : base(serial)
        {
        }

        public override void OnHit(Mobile attacker, IDamageable damageable, double damageBonus)
        {
            base.OnHit(attacker, damageable, damageBonus);
            GenieGifts.OnHitFlaw(this, attacker);
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write((int)0);
            writer.Write(m_SelfWound);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            reader.ReadInt();
            m_SelfWound = reader.ReadBool();
        }
    }

    public class GenieWarAxe : WarAxe, IGenieWeapon
    {
        private bool m_SelfWound;
        public bool SelfWound { get { return m_SelfWound; } set { m_SelfWound = value; } }

        [Constructable]
        public GenieWarAxe()
        {
        }

        public GenieWarAxe(Serial serial)
            : base(serial)
        {
        }

        public override void OnHit(Mobile attacker, IDamageable damageable, double damageBonus)
        {
            base.OnHit(attacker, damageable, damageBonus);
            GenieGifts.OnHitFlaw(this, attacker);
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write((int)0);
            writer.Write(m_SelfWound);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            reader.ReadInt();
            m_SelfWound = reader.ReadBool();
        }
    }
}
