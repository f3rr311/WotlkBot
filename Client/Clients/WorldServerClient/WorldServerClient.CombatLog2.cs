using System;
using System.Collections.Generic;
using System.Text;

using WotlkClient.Shared;
using WotlkClient.Network;
using WotlkClient.Constants;

namespace WotlkClient.Clients
{
    // ------------------------------------------------------------------------------------------
    // M1 combat packet handlers (task #37).
    //
    // Every layout below was transcribed from the SERVER'S OWN WRITER in C:\CoA\src\ac, never
    // guessed. The writer is named in the comment above each handler with its file:line so the
    // next person can re-verify in one jump. Guessing field order is how a harness silently
    // reports fabricated numbers.
    //
    // Two rules inherited from WorldServerClient.CombatLog.cs and kept here:
    //
    //   1. EVERY line carries caster= and target=. The bot is a real client sitting in a shared
    //      world; it sees other players' combat too. Damage with no attribution cannot be told
    //      apart from the bot's own and has already caused one wrong diagnosis.
    //
    //   2. NOTHING FAILS SILENTLY. Where a body/variant is not decoded we print a "<PREFIX>?"
    //      sighting line and stop parsing that packet rather than misreading fields. A silent
    //      failure reads downstream as a measurement of zero, which becomes a false balance
    //      conclusion. Handlers that can bound their own length also check for trailing bytes and
    //      report them — leftover bytes mean the layout is wrong, and that must be loud.
    // ------------------------------------------------------------------------------------------
    public partial class WorldServerClient
    {
        private static string Ts()
        {
            return DateTime.Now.ToString("HH:mm:ss.fff");
        }

        private static string Hex(ulong guid)
        {
            return "0x" + guid.ToString("X");
        }

        // src/ac SharedDefines.h:255 enum Powers
        private static string PowerName(uint power)
        {
            switch (power)
            {
                case 0: return "MANA";
                case 1: return "RAGE";
                case 2: return "FOCUS";
                case 3: return "ENERGY";
                case 4: return "HAPPINESS";
                case 5: return "RUNE";
                case 6: return "RUNIC_POWER";
                case 0xFFFFFFFE: return "HEALTH";
                default: return "POWER" + power;
            }
        }

        // src/ac Unit.h:82 enum VictimState
        private static string VictimStateName(byte state)
        {
            switch (state)
            {
                case 0: return "INTACT";
                case 1: return "HIT";
                case 2: return "DODGE";
                case 3: return "PARRY";
                case 4: return "INTERRUPT";
                case 5: return "BLOCKS";
                case 6: return "EVADES";
                case 7: return "IMMUNE";
                case 8: return "DEFLECTS";
                default: return "STATE" + state;
            }
        }

        // src/ac SharedDefines.h:1521 enum SpellMissInfo
        private static string MissName(byte miss)
        {
            switch (miss)
            {
                case 0: return "NONE";
                case 1: return "MISS";
                case 2: return "RESIST";
                case 3: return "DODGE";
                case 4: return "PARRY";
                case 5: return "BLOCK";
                case 6: return "EVADE";
                case 7: return "IMMUNE";
                case 8: return "IMMUNE2";
                case 9: return "DEFLECT";
                case 10: return "ABSORB";
                case 11: return "REFLECT";
                default: return "MISS" + miss;
            }
        }

        // ==========================================================================================
        // SMSG_ATTACKERSTATEUPDATE (0x14A) — melee/auto-attack swings.
        // Writer: src/ac src/server/game/Entities/Unit/Unit.cpp:6904 Unit::SendAttackStateUpdate
        //
        //   uint32 hitInfo
        //   packedGuid attacker
        //   packedGuid target
        //   uint32 fullDamage            (sum of the sub-damage blocks)
        //   uint32 overkill
        //   uint8  subDamageCount        (1 or 2 — MAX_ITEM_PROTO_DAMAGES)
        //   subDamageCount * { uint32 schoolMask; float damageF; uint32 damage; }
        //   if hitInfo & (FULL_ABSORB|PARTIAL_ABSORB): subDamageCount * uint32 absorb
        //   if hitInfo & (FULL_RESIST|PARTIAL_RESIST): subDamageCount * uint32 resist
        //   uint8  targetState
        //   uint32 unknown
        //   uint32 meleeSpellId
        //   if hitInfo & HITINFO_BLOCK:     uint32 blockedAmount
        //   if hitInfo & HITINFO_RAGE_GAIN: uint32 rageGain
        //   if hitInfo & HITINFO_UNK1:      uint32 + 10 * float + uint32   (48 debug bytes)
        //
        // This one carries melee DPS AND tank incoming damage, so a misparse would poison two
        // whole role measurements at once. The trailing-byte check below is the tripwire.
        // ==========================================================================================
        private const uint HITINFO_UNK1 = 0x00000001;
        private const uint HITINFO_OFFHAND = 0x00000004;
        private const uint HITINFO_MISS = 0x00000010;
        private const uint HITINFO_FULL_ABSORB = 0x00000020;
        private const uint HITINFO_PARTIAL_ABSORB = 0x00000040;
        private const uint HITINFO_FULL_RESIST = 0x00000080;
        private const uint HITINFO_PARTIAL_RESIST = 0x00000100;
        private const uint HITINFO_CRITICALHIT = 0x00000200;
        private const uint HITINFO_BLOCK = 0x00002000;
        private const uint HITINFO_GLANCING = 0x00010000;
        private const uint HITINFO_CRUSHING = 0x00020000;
        private const uint HITINFO_RAGE_GAIN = 0x00800000;

        [PacketHandlerAtribute(WorldServerOpCode.SMSG_ATTACKERSTATEUPDATE)]
        public void HandleAttackerStateUpdate(PacketIn packet)
        {
            try
            {
                uint hitInfo = packet.ReadUInt32();
                ulong attacker = ReadPackedGuid(packet);
                ulong target = ReadPackedGuid(packet);
                uint fullDamage = packet.ReadUInt32();
                uint overkill = packet.ReadUInt32();
                byte subCount = packet.ReadByte();

                // The server writes at most MAX_ITEM_PROTO_DAMAGES (2) blocks. Anything larger
                // means we are no longer aligned with the stream — say so instead of reading
                // hundreds of bytes of nonsense.
                if (subCount == 0 || subCount > 2)
                {
                    Console.WriteLine("MELEE? t=" + Ts()
                        + " hitInfo=0x" + hitInfo.ToString("X8")
                        + " subCount=" + subCount + " (implausible sub-damage count; body not decoded)"
                        + " caster=" + Hex(attacker) + " target=" + Hex(target));
                    return;
                }

                uint schoolMask = 0;
                for (int i = 0; i < subCount; i++)
                {
                    schoolMask |= packet.ReadUInt32();   // school of this sub-damage
                    packet.ReadUInt32();                 // float damage (same value, unused here)
                    packet.ReadUInt32();                 // uint32 sub damage (already in fullDamage)
                }

                uint absorb = 0;
                if ((hitInfo & (HITINFO_FULL_ABSORB | HITINFO_PARTIAL_ABSORB)) != 0)
                    for (int i = 0; i < subCount; i++)
                        absorb += packet.ReadUInt32();

                uint resist = 0;
                if ((hitInfo & (HITINFO_FULL_RESIST | HITINFO_PARTIAL_RESIST)) != 0)
                    for (int i = 0; i < subCount; i++)
                        resist += packet.ReadUInt32();

                byte targetState = packet.ReadByte();
                packet.ReadUInt32();                     // unknown attacker state
                uint meleeSpellId = packet.ReadUInt32();

                uint blocked = 0;
                if ((hitInfo & HITINFO_BLOCK) != 0)
                    blocked = packet.ReadUInt32();

                uint rageGain = 0;
                if ((hitInfo & HITINFO_RAGE_GAIN) != 0)
                    rageGain = packet.ReadUInt32();

                bool debugBlock = (hitInfo & HITINFO_UNK1) != 0;
                if (debugBlock && packet.Remaining >= 48)
                    packet.ReadBytes(48);                // uint32 + 10 floats + uint32, all zero

                Console.WriteLine("MELEE t=" + Ts()
                    + " dmg=" + fullDamage
                    + " overkill=" + overkill
                    + " absorb=" + absorb
                    + " resist=" + resist
                    + " blocked=" + blocked
                    + " crit=" + (((hitInfo & HITINFO_CRITICALHIT) != 0) ? 1 : 0)
                    + " miss=" + (((hitInfo & HITINFO_MISS) != 0) ? 1 : 0)
                    + " glancing=" + (((hitInfo & HITINFO_GLANCING) != 0) ? 1 : 0)
                    + " crushing=" + (((hitInfo & HITINFO_CRUSHING) != 0) ? 1 : 0)
                    + " offhand=" + (((hitInfo & HITINFO_OFFHAND) != 0) ? 1 : 0)
                    + " state=" + VictimStateName(targetState)
                    + " school=" + schoolMask
                    + " spell=" + meleeSpellId
                    + " rage=" + rageGain
                    + " hitInfo=0x" + hitInfo.ToString("X8")
                    + " caster=" + Hex(attacker) + " target=" + Hex(target));

                // Tripwire: the server sends exactly what it wrote. Bytes left over mean one of
                // the conditional blocks above is wrong for this variant, and every number on the
                // line just printed is suspect.
                if (packet.Remaining != 0)
                    Console.WriteLine("MELEE? t=" + Ts()
                        + " hitInfo=0x" + hitInfo.ToString("X8")
                        + " trailing=" + packet.Remaining
                        + " (variant not fully decoded — treat the MELEE line above as suspect)"
                        + " caster=" + Hex(attacker) + " target=" + Hex(target));
            }
            catch (Exception ex)
            {
                Console.WriteLine("MELEE? t=" + Ts() + " (parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "ATTACKERSTATEUPDATE parse: {0}", mUsername, ex.Message);
            }
        }

        // ==========================================================================================
        // SMSG_SPELLHEALLOG (0x150) — direct heals. This is ALL healer measurement.
        // Writer: src/ac Unit.cpp:8359 Unit::SendHealSpellLog
        //   packedGuid target, packedGuid caster, uint32 spellId,
        //   uint32 heal, uint32 overheal, uint32 absorb, uint8 critical, uint8 unused
        //
        // NOTE the enum entry for 0x150 in Constants.OpCodes.cs used to carry the VANILLA name
        // SMSG_HEALSPELL_ON_PLAYER_OBSOLETE. In 3.3.5a this opcode is SMSG_SPELLHEALLOG; the
        // constant was renamed (nothing referenced the old name).
        // ==========================================================================================
        [PacketHandlerAtribute(WorldServerOpCode.SMSG_SPELLHEALLOG)]
        public void HandleSpellHealLog(PacketIn packet)
        {
            try
            {
                ulong target = ReadPackedGuid(packet);
                ulong caster = ReadPackedGuid(packet);
                uint spellId = packet.ReadUInt32();
                uint heal = packet.ReadUInt32();
                uint overheal = packet.ReadUInt32();
                uint absorb = packet.ReadUInt32();
                byte crit = packet.ReadByte();

                // `heal` is the full heal; `overheal` is the part the target could not use and is
                // a SUBSET of it, exactly as `overkill` is a subset of `dmg` on the DMG line.
                NoteOwnHealing(caster, heal);
                Console.WriteLine("HEAL t=" + Ts()
                    + " spell=" + spellId
                    + " heal=" + heal
                    + " overheal=" + overheal
                    + " absorb=" + absorb
                    + " crit=" + crit
                    + " caster=" + Hex(caster) + " target=" + Hex(target));
            }
            catch (Exception ex)
            {
                Console.WriteLine("HEAL? t=" + Ts() + " (parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "SPELLHEALLOG parse: {0}", mUsername, ex.Message);
            }
        }

        // ==========================================================================================
        // SMSG_SPELLENERGIZELOG (0x151) — resource returns (mana/rage/energy/runic power).
        // Writer: src/ac Unit.cpp:8392 Unit::SendEnergizeSpellLog
        //   packedGuid target, packedGuid caster, uint32 spellId, uint32 powerType, uint32 amount
        //
        // Field order trap: powerType comes BEFORE the amount here, the opposite of the
        // ExecuteLogEffectTakeTargetPower body. Both were read from their own writers.
        // ==========================================================================================
        [PacketHandlerAtribute(WorldServerOpCode.SMSG_SPELLENERGIZELOG)]
        public void HandleSpellEnergizeLog(PacketIn packet)
        {
            try
            {
                ulong target = ReadPackedGuid(packet);
                ulong caster = ReadPackedGuid(packet);
                uint spellId = packet.ReadUInt32();
                uint powerType = packet.ReadUInt32();
                uint amount = packet.ReadUInt32();

                Console.WriteLine("ENERGIZE t=" + Ts()
                    + " spell=" + spellId
                    + " amount=" + amount
                    + " power=" + powerType
                    + " powerName=" + PowerName(powerType)
                    + " caster=" + Hex(caster) + " target=" + Hex(target));
            }
            catch (Exception ex)
            {
                Console.WriteLine("ENERGIZE? t=" + Ts() + " (parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "SPELLENERGIZELOG parse: {0}", mUsername, ex.Message);
            }
        }

        // ==========================================================================================
        // SMSG_AURA_UPDATE (0x496) / SMSG_AURA_UPDATE_ALL (0x495) — buffs, debuffs and CC.
        // Writers: src/ac SpellAuras.cpp:185 AuraApplication::BuildUpdatePacket
        //          src/ac SpellAuras.cpp:218 AuraApplication::ClientUpdate      (single entry)
        //          src/ac Player.cpp:12145   Player::GetAurasForTarget          (all entries)
        //
        //   packedGuid target
        //   then, per entry:
        //     uint8  slot
        //     uint32 spellId                       <-- 0 means THIS SLOT WAS REMOVED, entry ends
        //     uint8  flags                         (AFLAG_*)
        //     uint8  casterLevel
        //     uint8  stackAmount (or charges)
        //     if !(flags & AFLAG_CASTER):  packedGuid caster
        //     if  (flags & AFLAG_DURATION): uint32 maxDuration, uint32 duration
        //
        // SMSG_AURA_UPDATE carries exactly ONE entry; SMSG_AURA_UPDATE_ALL carries entries until
        // the packet ends (there is no count field — verified in GetAurasForTarget).
        //
        // The removal entry only names a SLOT, not a spell. That would make CC unverifiable
        // (you would see something end but not know what), so this handler keeps a per-target
        // slot->spell map and reports the spell id and the observed held time on removal.
        // AURA_UPDATE_ALL is an authoritative full snapshot, so it REPLACES that target's map.
        // ==========================================================================================
        private const byte AFLAG_CASTER = 0x08;
        private const byte AFLAG_POSITIVE = 0x10;
        private const byte AFLAG_DURATION = 0x20;

        private class AuraSlotState
        {
            public uint SpellId;
            public DateTime Applied;
        }

        private readonly Dictionary<ulong, Dictionary<byte, AuraSlotState>> mAuraSlots =
            new Dictionary<ulong, Dictionary<byte, AuraSlotState>>();

        private Dictionary<byte, AuraSlotState> AuraSlotsFor(ulong target)
        {
            Dictionary<byte, AuraSlotState> slots;
            if (!mAuraSlots.TryGetValue(target, out slots))
            {
                slots = new Dictionary<byte, AuraSlotState>();
                mAuraSlots[target] = slots;
            }
            return slots;
        }

        [PacketHandlerAtribute(WorldServerOpCode.SMSG_AURA_UPDATE)]
        public void HandleAuraUpdate(PacketIn packet)
        {
            try
            {
                ulong target = ReadPackedGuid(packet);
                lock (mAuraSlots)
                {
                    ReadOneAuraEntry(packet, target, AuraSlotsFor(target), "update");
                }

                if (packet.Remaining != 0)
                    Console.WriteLine("AURA? t=" + Ts()
                        + " trailing=" + packet.Remaining
                        + " (SMSG_AURA_UPDATE should carry exactly one entry — layout suspect)"
                        + " caster=0x0 target=" + Hex(target));
            }
            catch (Exception ex)
            {
                Console.WriteLine("AURA? t=" + Ts() + " (parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "AURA_UPDATE parse: {0}", mUsername, ex.Message);
            }
        }

        [PacketHandlerAtribute(WorldServerOpCode.SMSG_AURA_UPDATE_ALL)]
        public void HandleAuraUpdateAll(PacketIn packet)
        {
            try
            {
                ulong target = ReadPackedGuid(packet);

                // Full snapshot: rebuild this target's slot map from scratch so stale slots from a
                // previous session/visibility window cannot be reported later as bogus removals.
                lock (mAuraSlots)
                {
                    Dictionary<byte, AuraSlotState> previous = AuraSlotsFor(target);
                    Dictionary<byte, AuraSlotState> rebuilt = new Dictionary<byte, AuraSlotState>();
                    mAuraSlots[target] = rebuilt;

                    int guard = 0;
                    while (packet.Remaining > 0)
                    {
                        // Carry forward the original apply time for a slot we already knew about,
                        // so held-time on a later removal stays honest across snapshots.
                        if (!ReadOneAuraEntry(packet, target, rebuilt, "all", previous))
                            break;
                        if (++guard > 255)
                        {
                            Console.WriteLine("AURA? t=" + Ts()
                                + " (>255 entries in SMSG_AURA_UPDATE_ALL — bailing, layout suspect)"
                                + " caster=0x0 target=" + Hex(target));
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("AURA? t=" + Ts() + " (parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "AURA_UPDATE_ALL parse: {0}", mUsername, ex.Message);
            }
        }

        private bool ReadOneAuraEntry(PacketIn packet, ulong target,
                                      Dictionary<byte, AuraSlotState> slots, string src)
        {
            return ReadOneAuraEntry(packet, target, slots, src, null);
        }

        // Returns false when the entry could not be read in full (caller stops).
        private bool ReadOneAuraEntry(PacketIn packet, ulong target,
                                      Dictionary<byte, AuraSlotState> slots, string src,
                                      Dictionary<byte, AuraSlotState> carryFrom)
        {
            if (packet.Remaining < 5)
            {
                if (packet.Remaining > 0)
                    Console.WriteLine("AURA? t=" + Ts()
                        + " remaining=" + packet.Remaining
                        + " (truncated aura entry; not decoded)"
                        + " caster=0x0 target=" + Hex(target));
                return false;
            }

            byte slot = packet.ReadByte();
            uint spellId = packet.ReadUInt32();

            if (spellId == 0)
            {
                // Removal. The packet does not name the spell — recover it from the slot map.
                uint wasSpell = 0;
                long heldMs = -1;
                AuraSlotState prev;
                if (slots.TryGetValue(slot, out prev))
                {
                    wasSpell = prev.SpellId;
                    heldMs = (long)(DateTime.Now - prev.Applied).TotalMilliseconds;
                    slots.Remove(slot);
                    // drop it from self-state too, or the rotation keeps believing it is buffed
                    if (player != null && target == player.Guid.GetOldGuid())
                        NoteSelfAura(wasSpell, 0, false);
                }

                Console.WriteLine("AURA t=" + Ts()
                    + " op=remove"
                    + " spell=" + wasSpell
                    + " slot=" + slot
                    + " heldMs=" + heldMs
                    + " src=" + src
                    + (wasSpell == 0 ? " note=slot_not_tracked" : "")
                    + " caster=0x0 target=" + Hex(target));
                return true;
            }

            if (packet.Remaining < 3)
            {
                Console.WriteLine("AURA? t=" + Ts()
                    + " spell=" + spellId + " slot=" + slot
                    + " (truncated aura entry after spellId; not decoded)"
                    + " caster=0x0 target=" + Hex(target));
                return false;
            }

            byte flags = packet.ReadByte();
            byte casterLevel = packet.ReadByte();
            byte stacks = packet.ReadByte();

            // AFLAG_CASTER means the target cast it on itself, so the caster guid is omitted.
            ulong caster = target;
            if ((flags & AFLAG_CASTER) == 0)
                caster = ReadPackedGuid(packet);

            long maxDuration = -1;
            long duration = -1;
            if ((flags & AFLAG_DURATION) != 0)
            {
                if (packet.Remaining < 8)
                {
                    Console.WriteLine("AURA? t=" + Ts()
                        + " spell=" + spellId + " slot=" + slot
                        + " (AFLAG_DURATION set but duration fields truncated; not decoded)"
                        + " caster=" + Hex(caster) + " target=" + Hex(target));
                    return false;
                }
                maxDuration = packet.ReadUInt32();
                duration = packet.ReadUInt32();
            }

            DateTime applied = DateTime.Now;
            AuraSlotState existing;
            if (slots.TryGetValue(slot, out existing) && existing.SpellId == spellId)
                applied = existing.Applied;                    // refresh, not a fresh application
            else if (carryFrom != null && carryFrom.TryGetValue(slot, out existing) && existing.SpellId == spellId)
                applied = existing.Applied;                    // same aura seen in the prior snapshot

            AuraSlotState state = new AuraSlotState();
            state.SpellId = spellId;
            state.Applied = applied;
            slots[slot] = state;

            // STACK AWARENESS. A build-and-spend rotation must know its engine actually charged;
            // assuming it did is how a failed charge measures as "this engine is worthless".
            if (player != null && target == player.Guid.GetOldGuid())
                NoteSelfAura(spellId, stacks, true);     // this IS the apply path

            Console.WriteLine("AURA t=" + Ts()
                + " op=apply"
                + " spell=" + spellId
                + " slot=" + slot
                + " stacks=" + stacks
                + " maxDurMs=" + maxDuration
                + " durMs=" + duration
                + " positive=" + (((flags & AFLAG_POSITIVE) != 0) ? 1 : 0)
                + " casterLevel=" + casterLevel
                + " flags=0x" + flags.ToString("X2")
                + " src=" + src
                + " caster=" + Hex(caster) + " target=" + Hex(target));
            return true;
        }

        // ==========================================================================================
        // SMSG_THREAT_UPDATE (0x483) / SMSG_HIGHEST_THREAT_UPDATE (0x482) — tanking.
        // Writer: src/ac src/server/game/Combat/ThreatManager.cpp:871
        //         ThreatManager::SendThreatListToClients
        //
        //   packedGuid owner                     (the creature whose threat list this is)
        //   if HIGHEST_THREAT_UPDATE: packedGuid newVictim
        //   uint32 count
        //   count * { packedGuid unit; uint32 threat * 100; }
        //
        // "owner" is the mob; "unit" is whoever is on its threat table. The per-entry line is
        // written with caster=<unit> (who generated the threat) and target=<owner> (whose list it
        // is on) so it obeys the same attribution rule as the damage lines.
        // ==========================================================================================
        [PacketHandlerAtribute(WorldServerOpCode.SMSG_THREAT_UPDATE)]
        public void HandleThreatUpdate(PacketIn packet)
        {
            HandleThreatListInternal(packet, false);
        }

        [PacketHandlerAtribute(WorldServerOpCode.SMSG_HIGHEST_THREAT_UPDATE)]
        public void HandleHighestThreatUpdate(PacketIn packet)
        {
            HandleThreatListInternal(packet, true);
        }

        private void HandleThreatListInternal(PacketIn packet, bool highest)
        {
            ulong owner = 0;
            try
            {
                owner = ReadPackedGuid(packet);
                ulong newVictim = 0;
                if (highest)
                    newVictim = ReadPackedGuid(packet);

                uint count = packet.ReadUInt32();
                if (count > 512)
                {
                    Console.WriteLine("THREAT? t=" + Ts()
                        + " op=" + (highest ? "highest" : "list")
                        + " count=" + count + " (implausible entry count; body not decoded)"
                        + " caster=0x0 target=" + Hex(owner));
                    return;
                }

                if (highest)
                    Console.WriteLine("THREAT t=" + Ts()
                        + " op=highest"
                        + " owner=" + Hex(owner)
                        + " unit=" + Hex(newVictim)
                        + " n=" + count
                        + " caster=" + Hex(newVictim) + " target=" + Hex(owner));

                for (uint i = 0; i < count; i++)
                {
                    ulong unit = ReadPackedGuid(packet);
                    uint raw = packet.ReadUInt32();          // threat * 100
                    Console.WriteLine("THREAT t=" + Ts()
                        + " op=list"
                        + " owner=" + Hex(owner)
                        + " unit=" + Hex(unit)
                        + " threat=" + (raw / 100.0).ToString("F2")
                        + " rank=" + i
                        + " n=" + count
                        + " tanking=" + ((highest && i == 0) ? 1 : 0)
                        + " caster=" + Hex(unit) + " target=" + Hex(owner));
                }

                if (packet.Remaining != 0)
                    Console.WriteLine("THREAT? t=" + Ts()
                        + " trailing=" + packet.Remaining + " (layout suspect)"
                        + " caster=0x0 target=" + Hex(owner));
            }
            catch (Exception ex)
            {
                Console.WriteLine("THREAT? t=" + Ts() + " target=" + Hex(owner)
                    + " (parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "THREAT_UPDATE parse: {0}", mUsername, ex.Message);
            }
        }

        // SMSG_THREAT_CLEAR (0x485) — writer ThreatManager.cpp:856 SendClearAllThreatToClients
        //   packedGuid owner
        [PacketHandlerAtribute(WorldServerOpCode.SMSG_THREAT_CLEAR)]
        public void HandleThreatClear(PacketIn packet)
        {
            try
            {
                ulong owner = ReadPackedGuid(packet);
                Console.WriteLine("THREAT t=" + Ts()
                    + " op=clear"
                    + " owner=" + Hex(owner)
                    + " caster=0x0 target=" + Hex(owner));
            }
            catch (Exception ex)
            {
                Console.WriteLine("THREAT? t=" + Ts() + " (clear parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "THREAT_CLEAR parse: {0}", mUsername, ex.Message);
            }
        }

        // SMSG_THREAT_REMOVE (0x484) — writer ThreatManager.cpp:863 SendRemoveToClients
        //   packedGuid owner, packedGuid victim
        // Not in the M1 list but it is the other half of THREAT_CLEAR: without it, a unit dropping
        // off a threat table looks like it is still tanking forever.
        [PacketHandlerAtribute(WorldServerOpCode.SMSG_THREAT_REMOVE)]
        public void HandleThreatRemove(PacketIn packet)
        {
            try
            {
                ulong owner = ReadPackedGuid(packet);
                ulong unit = ReadPackedGuid(packet);
                Console.WriteLine("THREAT t=" + Ts()
                    + " op=remove"
                    + " owner=" + Hex(owner)
                    + " unit=" + Hex(unit)
                    + " caster=" + Hex(unit) + " target=" + Hex(owner));
            }
            catch (Exception ex)
            {
                Console.WriteLine("THREAT? t=" + Ts() + " (remove parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "THREAT_REMOVE parse: {0}", mUsername, ex.Message);
            }
        }

        // ==========================================================================================
        // SMSG_POWER_UPDATE (0x480) — rotation validity / resource starvation.
        // Writer: src/ac Unit.cpp:12486 (inside Unit::SetPower)
        //   packedGuid unit, uint8 powerType, uint32 value
        // ==========================================================================================
        [PacketHandlerAtribute(WorldServerOpCode.SMSG_POWER_UPDATE)]
        public void HandlePowerUpdate(PacketIn packet)
        {
            try
            {
                ulong unit = ReadPackedGuid(packet);
                byte powerType = packet.ReadByte();
                uint value = packet.ReadUInt32();

                // STORE it, do not merely print it - a rotation that cannot see its own resource
                // spams SPELL_FAILED_NO_POWER, which is what wrecked the FP-06 sweep.
                if (player != null && unit == player.Guid.GetOldGuid())
                    NoteSelfPower(powerType, (int)value);

                Console.WriteLine("POWER t=" + Ts()
                    + " power=" + powerType
                    + " powerName=" + PowerName(powerType)
                    + " value=" + value
                    + " caster=" + Hex(unit) + " target=" + Hex(unit));
            }
            catch (Exception ex)
            {
                Console.WriteLine("POWER? t=" + Ts() + " (parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "POWER_UPDATE parse: {0}", mUsername, ex.Message);
            }
        }

        // ==========================================================================================
        // SMSG_HEALTH_UPDATE (0x47F) — survival.
        //   packedGuid unit, uint32 health
        //
        // ⚠ HONESTY NOTE — this is the one layout in this file NOT taken from a server writer,
        // because AzerothCore HAS NO WRITER FOR IT. A full-tree grep of C:\CoA\src\ac finds
        // SMSG_HEALTH_UPDATE only in Opcodes.h and the STATUS_NEVER table in Opcodes.cpp; the
        // core ships health through UNIT_FIELD_HEALTH in SMSG_UPDATE_OBJECT instead. The layout
        // below is the 3.3.5a client's, kept so that if a module or a future core change ever
        // starts sending it we notice rather than log "Unhandled packet".
        //
        // ==> For M5 survival numbers, read health from the object-update path, NOT from this
        //     handler. Expect zero HP lines on a stock server; an HP line appearing is itself the
        //     news and should be investigated before it is trusted.
        // ==========================================================================================
        [PacketHandlerAtribute(WorldServerOpCode.SMSG_HEALTH_UPDATE)]
        public void HandleHealthUpdate(PacketIn packet)
        {
            try
            {
                ulong unit = ReadPackedGuid(packet);
                uint health = packet.ReadUInt32();

                Console.WriteLine("HP t=" + Ts()
                    + " health=" + health
                    + " note=opcode_unused_by_azerothcore"
                    + " caster=" + Hex(unit) + " target=" + Hex(unit));
            }
            catch (Exception ex)
            {
                Console.WriteLine("HP? t=" + Ts() + " (parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "HEALTH_UPDATE parse: {0}", mUsername, ex.Message);
            }
        }

        // ==========================================================================================
        // SMSG_SPELL_COOLDOWN (0x134) — the rotation engine's clock.
        // Writer: src/ac Unit.cpp:17082/17091 Unit::BuildCooldownPacket (both overloads)
        //   uint64 guid   <-- FULL 8-byte guid, NOT packed. This is the trap in this packet.
        //   uint8  flags
        //   repeated until end: { uint32 spellId; uint32 cooldownMs; }
        // ==========================================================================================
        [PacketHandlerAtribute(WorldServerOpCode.SMSG_SPELL_COOLDOWN)]
        public void HandleSpellCooldown(PacketIn packet)
        {
            try
            {
                ulong unit = packet.ReadUInt64();       // full guid — see comment above
                byte flags = packet.ReadByte();

                int n = 0;
                while (packet.Remaining >= 8)
                {
                    uint spellId = packet.ReadUInt32();
                    uint cooldown = packet.ReadUInt32();
                    Console.WriteLine("CD t=" + Ts()
                        + " spell=" + spellId
                        + " cooldownMs=" + cooldown
                        + " flags=0x" + flags.ToString("X2")
                        + " caster=" + Hex(unit) + " target=" + Hex(unit));
                    n++;
                }

                if (packet.Remaining != 0)
                    Console.WriteLine("CD? t=" + Ts()
                        + " trailing=" + packet.Remaining + " after=" + n
                        + " (layout suspect)"
                        + " caster=" + Hex(unit) + " target=" + Hex(unit));
            }
            catch (Exception ex)
            {
                Console.WriteLine("CD? t=" + Ts() + " (parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "SPELL_COOLDOWN parse: {0}", mUsername, ex.Message);
            }
        }

        // ==========================================================================================
        // SMSG_SPELL_GO (0x132) — the cast actually resolved, and on whom.
        // Writer: src/ac Spell.cpp:4781 Spell::SendSpellGo (+ Spell.cpp:4997 WriteSpellGoTargets)
        //
        //   packedGuid castItem-or-caster        (item guid if cast from an item, else the caster)
        //   packedGuid caster
        //   uint8  castCount
        //   uint32 spellId
        //   uint32 castFlags
        //   uint32 timestamp (ms)
        //   uint8  hitCount ; hitCount  * uint64 guid        <-- FULL guids, not packed
        //   uint8  missCount; missCount * { uint64 guid; uint8 missCondition;
        //                                   uint8 reflectResult if missCondition == REFLECT(11) }
        //   ... then SpellCastTargets (decoded below only far enough for the object target),
        //       then optional power-left / rune / ammo / trajectory blocks (NOT decoded).
        //
        // We deliberately stop after the target lists. Everything past them is conditional on
        // castFlags in ways that differ per class chassis, and a bad guess there would corrupt
        // nothing we need — but it would print numbers that look real. Better to stop.
        // ==========================================================================================
        [PacketHandlerAtribute(WorldServerOpCode.SMSG_SPELL_GO)]
        public void HandleSpellGo(PacketIn packet)
        {
            ulong caster = 0;
            uint spellId = 0;
            try
            {
                ReadPackedGuid(packet);                 // cast item, or the caster again
                caster = ReadPackedGuid(packet);
                byte castCount = packet.ReadByte();
                spellId = packet.ReadUInt32();
                uint castFlags = packet.ReadUInt32();
                uint timestamp = packet.ReadUInt32();

                byte hitCount = packet.ReadByte();
                if (packet.Remaining < hitCount * 8)
                {
                    Console.WriteLine("CASTGO? t=" + Ts()
                        + " spell=" + spellId + " hitCount=" + hitCount
                        + " remaining=" + packet.Remaining
                        + " (hit list does not fit; not decoded)"
                        + " caster=" + Hex(caster) + " target=0x0");
                    return;
                }

                ulong[] hits = new ulong[hitCount];
                for (int i = 0; i < hitCount; i++)
                    hits[i] = packet.ReadUInt64();

                byte missCount = packet.ReadByte();
                ulong[] missGuids = new ulong[missCount];
                byte[] missCodes = new byte[missCount];
                for (int i = 0; i < missCount; i++)
                {
                    if (packet.Remaining < 9)
                    {
                        Console.WriteLine("CASTGO? t=" + Ts()
                            + " spell=" + spellId + " missIdx=" + i + " missCount=" + missCount
                            + " (miss list truncated; not decoded)"
                            + " caster=" + Hex(caster) + " target=0x0");
                        return;
                    }
                    missGuids[i] = packet.ReadUInt64();
                    missCodes[i] = packet.ReadByte();
                    if (missCodes[i] == 11 && packet.Remaining >= 1)   // SPELL_MISS_REFLECT
                        packet.ReadByte();                             // reflectResult
                }

                if (hitCount == 0 && missCount == 0)
                {
                    // Self-buffs and ground-targeted casts legitimately land on nobody.
                    Console.WriteLine("CASTGO t=" + Ts()
                        + " spell=" + spellId
                        + " castCount=" + castCount
                        + " flags=0x" + castFlags.ToString("X8")
                        + " ts=" + timestamp
                        + " hits=0 misses=0 hit=0 idx=0"
                        + " caster=" + Hex(caster) + " target=0x0");
                }

                for (int i = 0; i < hitCount; i++)
                    Console.WriteLine("CASTGO t=" + Ts()
                        + " spell=" + spellId
                        + " castCount=" + castCount
                        + " flags=0x" + castFlags.ToString("X8")
                        + " ts=" + timestamp
                        + " hits=" + hitCount + " misses=" + missCount
                        + " hit=1 idx=" + i
                        + " caster=" + Hex(caster) + " target=" + Hex(hits[i]));

                for (int i = 0; i < missCount; i++)
                    Console.WriteLine("CASTGO t=" + Ts()
                        + " spell=" + spellId
                        + " castCount=" + castCount
                        + " flags=0x" + castFlags.ToString("X8")
                        + " ts=" + timestamp
                        + " hits=" + hitCount + " misses=" + missCount
                        + " hit=0 idx=" + i
                        + " missCode=" + missCodes[i]
                        + " missName=" + MissName(missCodes[i])
                        + " caster=" + Hex(caster) + " target=" + Hex(missGuids[i]));
            }
            catch (Exception ex)
            {
                Console.WriteLine("CASTGO? t=" + Ts() + " spell=" + spellId
                    + " caster=" + Hex(caster) + " (parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "SPELL_GO parse: {0}", mUsername, ex.Message);
            }
        }

        // ==========================================================================================
        // SMSG_SPELL_START (0x131) — a cast began (cast bar opened).
        // Writer: src/ac Spell.cpp:4701 Spell::SendSpellStart
        //   packedGuid castItem-or-caster, packedGuid caster, uint8 castCount,
        //   uint32 spellId, uint32 castFlags, int32 castTimeMs,
        //   then SpellCastTargets — decoded here ONLY as far as the object target guid
        //   (uint32 targetMask, then packedGuid if the mask names an object target).
        //   Everything after that is left alone.
        //
        // Pairing START with GO is what makes "attempted vs landed" and GCD occupancy (task #29)
        // measurable, so the cast time is carried on the line.
        // ==========================================================================================
        private const uint TARGET_FLAG_OBJECT_MASK = 0x00000002    // UNIT
                                                   | 0x00008000    // CORPSE_ALLY
                                                   | 0x00000800    // GAMEOBJECT
                                                   | 0x00000200    // CORPSE_ENEMY
                                                   | 0x00010000;   // UNIT_MINIPET

        [PacketHandlerAtribute(WorldServerOpCode.SMSG_SPELL_START)]
        public void HandleSpellStart(PacketIn packet)
        {
            ulong caster = 0;
            uint spellId = 0;
            try
            {
                ReadPackedGuid(packet);                 // cast item, or the caster again
                caster = ReadPackedGuid(packet);
                byte castCount = packet.ReadByte();
                spellId = packet.ReadUInt32();
                uint castFlags = packet.ReadUInt32();
                int castTime = packet.ReadInt32();

                ulong objTarget = 0;
                if (packet.Remaining >= 4)
                {
                    uint targetMask = packet.ReadUInt32();
                    if ((targetMask & TARGET_FLAG_OBJECT_MASK) != 0 && packet.Remaining >= 1)
                        objTarget = ReadPackedGuid(packet);
                }

                Console.WriteLine("CASTSTART t=" + Ts()
                    + " spell=" + spellId
                    + " castCount=" + castCount
                    + " castTimeMs=" + castTime
                    + " flags=0x" + castFlags.ToString("X8")
                    + " caster=" + Hex(caster) + " target=" + Hex(objTarget));
            }
            catch (Exception ex)
            {
                Console.WriteLine("CASTSTART? t=" + Ts() + " spell=" + spellId
                    + " caster=" + Hex(caster) + " (parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "SPELL_START parse: {0}", mUsername, ex.Message);
            }
        }

        // ==========================================================================================
        // SMSG_SPELLLOGEXECUTE (0x24C) — per-effect execution log (drains, interrupts, summons,
        // extra attacks, item create/destroy, durability, resurrect).
        // Writer: src/ac Spell.cpp:5057 Spell::SendLogExecute, bodies at Spell.cpp:5092-5155,
        //         target counter written by Spell.cpp:8546 Spell::InitEffectExecuteData.
        //
        //   packedGuid caster
        //   uint32 spellId
        //   uint32 effectCount
        //   effectCount * { uint32 spellEffect; uint32 targetCount; targetCount * <body> }
        //
        // The <body> shape depends on the effect id (values from src/ac SharedDefines.h:774+):
        //   packedGuid only ...... 18 RESURRECT, 19 is NOT this, 28 SUMMON, 33 OPEN_LOCK,
        //                          50 TRANS_DOOR, 76 SUMMON_OBJECT_WILD, 83 DUEL,
        //                          89 GAMEOBJECT_SET_DESTRUCTION_STATE, 102 DISMISS_PET,
        //                          104-107 SUMMON_OBJECT_SLOT1-4, 113 RESURRECT_NEW
        //   pguid+u32 ............ 19 ADD_EXTRA_ATTACKS, 68 INTERRUPT_CAST
        //   pguid+u32+u32+float .. 8 POWER_DRAIN, 62 POWER_BURN
        //   pguid+i32+i32 ........ 111 DURABILITY_DAMAGE
        //   u32 entry ............ 24 CREATE_ITEM, 101 FEED_PET, 157 CREATE_ITEM_2
        //
        // An unknown effect id has an unknown body LENGTH, so there is no way to skip it and stay
        // aligned. In that case we print a SPELLEXEC? sighting and stop parsing this packet —
        // the alternative is emitting confident garbage for every effect after it.
        // ==========================================================================================
        [PacketHandlerAtribute(WorldServerOpCode.SMSG_SPELLLOGEXECUTE)]
        public void HandleSpellLogExecute(PacketIn packet)
        {
            ulong caster = 0;
            uint spellId = 0;
            try
            {
                caster = ReadPackedGuid(packet);
                spellId = packet.ReadUInt32();
                uint effectCount = packet.ReadUInt32();

                if (effectCount > 3)
                {
                    Console.WriteLine("SPELLEXEC? t=" + Ts()
                        + " spell=" + spellId + " effects=" + effectCount
                        + " (>MAX_SPELL_EFFECTS; body not decoded)"
                        + " caster=" + Hex(caster) + " target=0x0");
                    return;
                }

                for (uint e = 0; e < effectCount; e++)
                {
                    uint effect = packet.ReadUInt32();
                    uint targetCount = packet.ReadUInt32();

                    if (targetCount > 512)
                    {
                        Console.WriteLine("SPELLEXEC? t=" + Ts()
                            + " spell=" + spellId + " effect=" + effect
                            + " targets=" + targetCount + " (implausible target count; stopping)"
                            + " caster=" + Hex(caster) + " target=0x0");
                        return;
                    }

                    for (uint i = 0; i < targetCount; i++)
                    {
                        if (!ReadSpellExecEffectBody(packet, caster, spellId, effect))
                            return;                     // sighting already printed; stay aligned by stopping
                    }
                }

                if (packet.Remaining != 0)
                    Console.WriteLine("SPELLEXEC? t=" + Ts()
                        + " spell=" + spellId + " trailing=" + packet.Remaining
                        + " (layout suspect)"
                        + " caster=" + Hex(caster) + " target=0x0");
            }
            catch (Exception ex)
            {
                Console.WriteLine("SPELLEXEC? t=" + Ts() + " spell=" + spellId
                    + " caster=" + Hex(caster) + " (parse threw: " + ex.Message + ")");
                Log.WriteLine(LogType.Error, "SPELLLOGEXECUTE parse: {0}", mUsername, ex.Message);
            }
        }

        private bool ReadSpellExecEffectBody(PacketIn packet, ulong caster, uint spellId, uint effect)
        {
            switch (effect)
            {
                case 8:     // SPELL_EFFECT_POWER_DRAIN
                case 62:    // SPELL_EFFECT_POWER_BURN
                    {
                        ulong t = ReadPackedGuid(packet);
                        uint taken = packet.ReadUInt32();
                        uint powerType = packet.ReadUInt32();
                        float mult = packet.ReadFloat();
                        Console.WriteLine("SPELLEXEC t=" + Ts()
                            + " spell=" + spellId + " effect=" + effect + " kind=POWER_DRAIN"
                            + " amount=" + taken
                            + " power=" + powerType + " powerName=" + PowerName(powerType)
                            + " gainMult=" + mult.ToString("F2")
                            + " caster=" + Hex(caster) + " target=" + Hex(t));
                        return true;
                    }

                case 19:    // SPELL_EFFECT_ADD_EXTRA_ATTACKS
                case 68:    // SPELL_EFFECT_INTERRUPT_CAST
                    {
                        ulong t = ReadPackedGuid(packet);
                        uint v = packet.ReadUInt32();
                        Console.WriteLine("SPELLEXEC t=" + Ts()
                            + " spell=" + spellId + " effect=" + effect
                            + " kind=" + (effect == 19 ? "EXTRA_ATTACKS" : "INTERRUPT_CAST")
                            + " value=" + v
                            + " caster=" + Hex(caster) + " target=" + Hex(t));
                        return true;
                    }

                case 111:   // SPELL_EFFECT_DURABILITY_DAMAGE
                    {
                        ulong t = ReadPackedGuid(packet);
                        int itemId = packet.ReadInt32();
                        int slot = packet.ReadInt32();
                        Console.WriteLine("SPELLEXEC t=" + Ts()
                            + " spell=" + spellId + " effect=" + effect + " kind=DURABILITY_DAMAGE"
                            + " item=" + itemId + " slot=" + slot
                            + " caster=" + Hex(caster) + " target=" + Hex(t));
                        return true;
                    }

                case 24:    // SPELL_EFFECT_CREATE_ITEM
                case 101:   // SPELL_EFFECT_FEED_PET       (writes the destroyed item's entry)
                case 157:   // SPELL_EFFECT_CREATE_ITEM_2
                    {
                        uint entry = packet.ReadUInt32();
                        Console.WriteLine("SPELLEXEC t=" + Ts()
                            + " spell=" + spellId + " effect=" + effect + " kind=ITEM"
                            + " entry=" + entry
                            + " caster=" + Hex(caster) + " target=" + Hex(caster));
                        return true;
                    }

                case 18:    // SPELL_EFFECT_RESURRECT
                case 28:    // SPELL_EFFECT_SUMMON
                case 33:    // SPELL_EFFECT_OPEN_LOCK
                case 50:    // SPELL_EFFECT_TRANS_DOOR
                case 76:    // SPELL_EFFECT_SUMMON_OBJECT_WILD
                case 83:    // SPELL_EFFECT_DUEL
                case 89:    // SPELL_EFFECT_GAMEOBJECT_SET_DESTRUCTION_STATE
                case 102:   // SPELL_EFFECT_DISMISS_PET
                case 104:   // SPELL_EFFECT_SUMMON_OBJECT_SLOT1
                case 105:   // SPELL_EFFECT_SUMMON_OBJECT_SLOT2
                case 106:   // SPELL_EFFECT_SUMMON_OBJECT_SLOT3
                case 107:   // SPELL_EFFECT_SUMMON_OBJECT_SLOT4
                case 113:   // SPELL_EFFECT_RESURRECT_NEW
                    {
                        ulong t = ReadPackedGuid(packet);
                        Console.WriteLine("SPELLEXEC t=" + Ts()
                            + " spell=" + spellId + " effect=" + effect + " kind=OBJECT"
                            + " caster=" + Hex(caster) + " target=" + Hex(t));
                        return true;
                    }

                default:
                    // Unknown body LENGTH — cannot skip it and stay aligned, so stop.
                    Console.WriteLine("SPELLEXEC? t=" + Ts()
                        + " spell=" + spellId + " effect=" + effect
                        + " remaining=" + packet.Remaining
                        + " (effect body not decoded — stopped to avoid misparsing the rest)"
                        + " caster=" + Hex(caster) + " target=0x0");
                    return false;
            }
        }
    }
}
