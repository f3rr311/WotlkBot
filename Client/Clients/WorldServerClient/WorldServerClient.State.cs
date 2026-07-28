using System;
using System.Collections.Generic;

namespace WotlkClient.Clients
{
    /// <summary>
    /// BOT SELF-AWARENESS: power, health, auras/stacks and the last cast result.
    ///
    /// WHY THIS EXISTS. The bot already RECEIVED all of this and only ever printed it, so it acted
    /// completely blind: it fired a fixed number of casts at a fixed cadence regardless of whether
    /// it had the resource, whether the spell was off cooldown, or whether the engine it was
    /// supposed to be charging had actually stacked.
    ///
    /// That is not a cosmetic gap. The FP-06 engine sweep produced 312 CAST FAILURES across 15
    /// branches and only 2 clean runs, dominated by:
    ///     85 SPELL_FAILED_NO_POWER   - rage/energy classes have NO resource, because the bot never
    ///                                  autoattacks (Program.cs skipped CMSG_ATTACKSWING on purpose
    ///                                  to keep melee out of the parse). A Warrior starts combat at
    ///                                  0 rage, so every single one of Templar's 40 casts failed.
    ///     67 SPELL_FAILED_NOT_READY  - fixed cadence instead of real cooldowns.
    /// Four rounds of tuning the sweep could not fix either, because the bot had no idea what state
    /// it was in. Storing what it is already told is the whole fix.
    /// </summary>
    public partial class WorldServerClient
    {
        // ---- power (0 mana, 1 rage, 2 focus, 3 energy, 6 runic power) ---------------------------
        public readonly int[] CurrentPower = new int[8];
        public readonly int[] MaxPower = new int[8];
        public DateTime LastPowerUpdate = DateTime.MinValue;

        // ---- health and DEATH --------------------------------------------------------------------
        // These were declared and NEVER ASSIGNED, because `ReadValuesUpdateBlock` discarded every
        // update field it parsed. The bot therefore could not tell it had died: a corpse kept
        // running its rotation, every cast failed, and the run produced zeros until its window
        // expired - indistinguishable from "this branch deals no damage" (task #72).
        public int CurrentHealth = 0;
        public int MaxHealthValue = 0;
        public bool IsDead = false;
        public DateTime DiedAt = DateTime.MinValue;
        public int DeathCount = 0;

        /// <summary>Store our own health and detect the 0-crossing in both directions.</summary>
        public void NoteSelfVitals(int cur, int max)
        {
            if (max > 0)
                MaxHealthValue = max;
            int prev = CurrentHealth;
            CurrentHealth = cur;

            if (cur <= 0 && !IsDead && MaxHealthValue > 0 && prev > 0)
            {
                IsDead = true;
                DiedAt = DateTime.Now;
                DeathCount++;
                // Announce it. A death must be VISIBLE in the log, because a silent death looks
                // exactly like a branch that does no damage - and a measurement taken across one
                // must be suppressed, not reported.
                Console.WriteLine("PARSE: DIED t=" + DateTime.Now.ToString("HH:mm:ss.ffffff")
                                  + " deaths=" + DeathCount);
            }
            else if (cur > 0 && IsDead)
            {
                IsDead = false;
                Console.WriteLine("PARSE: ALIVE again t=" + DateTime.Now.ToString("HH:mm:ss.ffffff")
                                  + " hp=" + cur + "/" + MaxHealthValue);
            }
        }

        public int HealthPct()
        {
            return MaxHealthValue <= 0 ? 100 : (int)(100L * CurrentHealth / MaxHealthValue);
        }

        // ---- auras on SELF: spellId -> stack count. THIS is how a rotation knows the engine is
        //      actually charging rather than assuming it did. A silent failure to stack otherwise
        //      reads as "this engine is worthless".
        public readonly Dictionary<uint, int> SelfAuras = new Dictionary<uint, int>();

        // ---- THROUGHPUT: what this character has actually put out -------------------------------
        // Damage arrives on TWO opcodes and healing on a third, and counting only the first is how
        // 45% of a damage-over-time kit's output went missing from every sweep:
        //   SMSG_SPELLNONMELEEDAMAGELOG -> "DMG"   direct hits
        //   SMSG_PERIODICAURALOG        -> "TICK"  DoT/HoT ticks   <-- was never counted
        //   SMSG_SPELLHEALLOG           -> "HEAL"  direct heals    <-- ALL healer measurement
        // A healer branch produces almost no DMG at all, so without HEAL there is nothing to
        // measure it by.
        public long TotalDirect, TotalPeriodic, TotalHealing;
        public int DirectHits, PeriodicTicks, HealCasts;

        public void NoteDamage(long amount, bool periodic)
        {
            if (periodic) { TotalPeriodic += amount; PeriodicTicks++; }
            else { TotalDirect += amount; DirectHits++; }
        }

        /// <summary>Count damage only when WE dealt it - with 8 bots on one dummy every log is
        /// full of the neighbours' output, and totalling that would be meaningless.</summary>
        public void NoteOwnDamage(ulong casterGuid, long amount, bool periodic)
        {
            if (player != null && casterGuid == player.Guid.GetOldGuid())
                NoteDamage(amount, periodic);
        }

        public void NoteOwnHealing(ulong casterGuid, long amount)
        {
            if (player != null && casterGuid == player.Guid.GetOldGuid())
                NoteHealing(amount);
        }

        public void NoteHealing(long amount)
        {
            TotalHealing += amount;
            HealCasts++;
        }

        public void ResetThroughput()
        {
            TotalDirect = TotalPeriodic = TotalHealing = 0;
            DirectHits = PeriodicTicks = HealCasts = 0;
        }

        // ---- last cast result, so a rotation can react instead of blindly continuing ------------
        public uint LastFailedSpell = 0;
        public byte LastFailedCode = 0;
        public DateTime LastFailedAt = DateTime.MinValue;

        public void NoteSelfPower(byte type, int value)
        {
            if (type < CurrentPower.Length)
            {
                CurrentPower[type] = value;
                if (value > MaxPower[type])
                    MaxPower[type] = value;     // best-known ceiling; we are never told the max
                LastPowerUpdate = DateTime.Now;
            }
        }

        public void NoteSelfAura(uint spellId, int stacks, bool applied)
        {
            lock (SelfAuras)
            {
                if (!applied)
                    SelfAuras.Remove(spellId);
                else
                    SelfAuras[spellId] = stacks < 1 ? 1 : stacks;
            }
        }

        public int AuraStacks(uint spellId)
        {
            lock (SelfAuras)
            {
                int n;
                return SelfAuras.TryGetValue(spellId, out n) ? n : 0;
            }
        }

        public void NoteCastFail(uint spellId, byte code)
        {
            LastFailedSpell = spellId;
            LastFailedCode = code;
            LastFailedAt = DateTime.Now;
        }

        /// <summary>Percentage of the best-seen ceiling for a power type, or 100 if never seen.</summary>
        public int PowerPct(byte type)
        {
            if (type >= CurrentPower.Length || MaxPower[type] <= 0)
                return 100;
            return (int)(100L * CurrentPower[type] / MaxPower[type]);
        }

        public string DescribeState()
        {
            var parts = new List<string>();
            string[] names = { "mana", "rage", "focus", "energy", "happiness", "rune", "runic", "?" };
            for (byte i = 0; i < CurrentPower.Length; i++)
                if (MaxPower[i] > 0)
                    parts.Add(string.Format("{0}={1}/{2}", names[i], CurrentPower[i], MaxPower[i]));
            if (MaxHealthValue > 0)
                parts.Add(string.Format("hp={0}/{1}({2}%){3}", CurrentHealth, MaxHealthValue,
                                        HealthPct(), IsDead ? " DEAD" : ""));
            lock (SelfAuras)
                parts.Add("auras=" + SelfAuras.Count);
            parts.Add(string.Format("dmg={0}({1}) tick={2}({3}) heal={4}({5}) total={6}",
                                    TotalDirect, DirectHits, TotalPeriodic, PeriodicTicks,
                                    TotalHealing, HealCasts, TotalDirect + TotalPeriodic));
            return string.Join(" ", parts.ToArray());
        }
    }
}
