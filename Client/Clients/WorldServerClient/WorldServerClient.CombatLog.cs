using System;

using WotlkClient.Shared;
using WotlkClient.Network;
using WotlkClient.Constants;

namespace WotlkClient.Clients
{
    public partial class WorldServerClient
    {
        private ulong ReadPackedGuid(PacketIn p)
        {
            byte mask = p.ReadByte();
            ulong guid = 0;
            for (int i = 0; i < 8; i++)
                if ((mask & (1 << i)) != 0)
                    guid |= (ulong)p.ReadByte() << (8 * i);
            return guid;
        }

        // Logout bookkeeping. CMSG_LOGOUT_REQUEST is not instant: the server replies with
        // SMSG_LOGOUT_RESPONSE (a non-zero reason means DENIED — being in combat is the usual
        // one for a bot that just hit a dummy), then runs a ~20s timer before SMSG_LOGOUT_COMPLETE.
        // Exiting before COMPLETE arrives leaves the character in-world and `online=1`.
        public volatile bool LogoutComplete = false;
        public volatile int LogoutDenied = -1;

        [PacketHandlerAtribute(WorldServerOpCode.SMSG_LOGOUT_RESPONSE)]
        public void HandleLogoutResponse(PacketIn packet)
        {
            try
            {
                uint reason = packet.ReadUInt32();
                LogoutDenied = (int)reason;
                if (reason != 0)
                    Console.WriteLine("PARSE: logout DENIED reason=" + reason + " (17 = in combat)");
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogType.Error, "LOGOUT_RESPONSE parse: {0}", mUsername, ex.Message);
            }
        }

        [PacketHandlerAtribute(WorldServerOpCode.SMSG_LOGOUT_COMPLETE)]
        public void HandleLogoutComplete(PacketIn packet)
        {
            LogoutComplete = true;
            Console.WriteLine("PARSE: logout complete — character is out of the world");
        }

        // SMSG_CAST_RESULT / SMSG_CAST_FAILED (0x0130) — why a cast did not happen.
        //
        // Without this, a rejected cast is COMPLETELY silent: the bot prints "cast 6/6 DONE",
        // the server logs nothing, and the only symptom is missing DMG lines — which looks
        // identical to "the balance change broke the game." Layout from Spell::SendCastResult:
        // uint8 castCount, uint32 spellId, uint8 result (SPELL_CAST_OK == 255).
        [PacketHandlerAtribute(WorldServerOpCode.SMSG_CAST_RESULT)]
        public void HandleCastResult(PacketIn packet)
        {
            try
            {
                byte castCount = packet.ReadByte();
                uint spellId = packet.ReadUInt32();
                byte result = packet.ReadByte();
                if (result == 255) return;                       // SPELL_CAST_OK — nothing to report
                NoteCastFail(spellId, result);   // so a rotation can back off instead of spamming
                Console.WriteLine("CASTFAIL t=" + DateTime.Now.ToString("HH:mm:ss.fff")
                    + " spell=" + spellId + " code=" + result + " castCount=" + castCount);
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogType.Error, "CASTRESULT parse: {0}", mUsername, ex.Message);
            }
        }

        // SMSG_SPELLNONMELEEDAMAGELOG (0x0250) — the server's authoritative per-cast spell
        // damage. Printed straight to stdout so the bot reports its own DPS: the CoA balance
        // harness reads these "DMG" lines instead of the user's client combat log.
        [PacketHandlerAtribute(WorldServerOpCode.SMSG_SPELLNONMELEEDAMAGELOG)]
        public void HandleSpellNonMeleeDamageLog(PacketIn packet)
        {
            try
            {
                ulong target = ReadPackedGuid(packet);
                ulong caster = ReadPackedGuid(packet);
                uint spellId = packet.ReadUInt32();
                uint damage = packet.ReadUInt32();
                uint overkill = packet.ReadUInt32();
                byte school = packet.ReadByte();
                uint absorb = packet.ReadUInt32();
                uint resist = packet.ReadUInt32();
                packet.ReadByte();                       // physicalLog
                packet.ReadByte();                       // unused
                uint blocked = packet.ReadUInt32();
                uint hitInfo = packet.ReadUInt32();
                bool crit = (hitInfo & 0x2) != 0;
                // `damage` is the spell's full output; `overkill` is only the part beyond the
                // target's remaining HP (huge here because the dummy sits near 0 HP) — it is
                // NOT added to the damage, it is a subset of it.

                // caster/target are ESSENTIAL, not decoration: the bot is a real client and logs
                // the whole area's combat. Without attribution there is no way to tell the bot's
                // own damage from a nearby player's or their minions', and "spell X did N damage"
                // becomes an unfalsifiable claim. Filtering on spell id alone is not enough —
                // several unrelated spells here share both a name and a damage range.
                NoteOwnDamage(caster, damage, false);
                Console.WriteLine("DMG t=" + DateTime.Now.ToString("HH:mm:ss.fff")
                    + " spell=" + spellId + " dmg=" + damage + " overkill=" + overkill
                    + " crit=" + (crit ? 1 : 0) + " absorb=" + absorb + " resist=" + resist
                    + " caster=0x" + caster.ToString("X") + " target=0x" + target.ToString("X"));
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogType.Error, "SPELLNONMELEE parse: {0}", mUsername, ex.Message);
            }
        }

        // SMSG_PERIODICAURALOG (0x24E) — DoT/HoT ticks. Layout taken from the server's own
        // Unit::SendPeriodicAuraLog (src/ac Unit.cpp:6814): packedGuid target, packedGuid caster,
        // uint32 spellId, uint32 count, then per entry uint32 auraType + type-specific body.
        [PacketHandlerAtribute(WorldServerOpCode.SMSG_PERIODICAURALOG)]
        public void HandlePeriodicAuraLog(PacketIn packet)
        {
            try
            {
                ulong target = ReadPackedGuid(packet);
                ulong caster = ReadPackedGuid(packet);
                uint spellId = packet.ReadUInt32();
                uint count = packet.ReadUInt32();
                for (uint i = 0; i < count; i++)
                {
                    uint auraType = packet.ReadUInt32();
                    if (auraType == 3 || auraType == 89)            // PERIODIC_DAMAGE / _PERCENT
                    {
                        uint damage = packet.ReadUInt32();
                        uint overkill = packet.ReadUInt32();
                        packet.ReadUInt32();                        // school mask
                        uint absorb = packet.ReadUInt32();
                        uint resist = packet.ReadUInt32();
                        byte crit = packet.ReadByte();
                        NoteOwnDamage(caster, damage, true);
                        Console.WriteLine("TICK t=" + DateTime.Now.ToString("HH:mm:ss.fff")
                            + " spell=" + spellId + " dmg=" + damage + " overkill=" + overkill
                            + " crit=" + crit + " absorb=" + absorb + " resist=" + resist
                            + " caster=0x" + caster.ToString("X") + " target=0x" + target.ToString("X"));
                    }
                    else if (auraType == 8 || auraType == 20)       // PERIODIC_HEAL / OBS_MOD_HEALTH
                    {
                        uint heal = packet.ReadUInt32();
                        uint overheal = packet.ReadUInt32();
                        uint absorb = packet.ReadUInt32();
                        byte crit = packet.ReadByte();
                        Console.WriteLine("HOT t=" + DateTime.Now.ToString("HH:mm:ss.fff")
                            + " spell=" + spellId + " heal=" + heal + " overheal=" + overheal
                            + " crit=" + crit
                            + " caster=0x" + caster.ToString("X") + " target=0x" + target.ToString("X"));
                    }
                    else
                    {
                        // energize/leech/other — body layout differs; log the sighting and stop
                        // parsing this packet rather than misreading fields.
                        Console.WriteLine("TICK? t=" + DateTime.Now.ToString("HH:mm:ss.fff")
                            + " spell=" + spellId + " auraType=" + auraType + " (body not decoded)");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogType.Error, "PERIODICAURALOG parse: {0}", mUsername, ex.Message);
            }
        }
    }
}
