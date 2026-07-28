using System;
using WotlkClient;
using WotlkClient.Clients;
using IronPython;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using System.Runtime.InteropServices;
using System.IO;
using System.Reflection;
using static Community.CsharpSqlite.Sqlite3;
using WotlkClient.Constants;

namespace WotlkBot
{
    partial class Program
    {
        static LogonServerClient lclient;
        static WorldServerClient wclient;
        static string accountName;
        static string charName;
        static uint parseSpell = 960086;   // Lich Bolt (max rank) — default parse spell
        static int parseCount = 30;
        static int parseDelayMs = 1800;
        static ulong parseTargetGuid = 0;  // explicit target (hex arg) — bypasses object scan
        static bool stayLoggedIn = false;  // "stay" arg: persistent session driven by CmdFile
        static volatile bool inWorld = false;   // set once CharLoginComplete fires (login watchdog)

        // Per-INSTANCE command file. This must never be a shared constant: healing and tanking
        // can only be measured with several bots running at once (one takes damage, another
        // heals it), and a single shared cmd.txt would have every instance racing to read and
        // truncate the same file — commands would land on whichever bot got there first.
        // Keyed on character name, so `cmd-Coabot.txt` drives Coabot and nothing else.
        static string CmdFile
        {
            get { return @"C:\CoA\WotlkBot\cmd-" + charName + ".txt"; }
        }

        public static void Main(string[] args)
        {
            var so = new System.IO.StreamWriter(System.Console.OpenStandardOutput());
            so.AutoFlush = true;
            System.Console.SetOut(so);   // flush parse diagnostics immediately (no exit-only buffer)
            string password;
            string host;
            int port = 3724;

            if (args.Length < 4)
            {
                System.Console.WriteLine("Usage: WotlkBot <host> <accountname> <password> <charname>");
                System.Console.WriteLine("Press any key to continue...");
                System.Console.ReadKey();
                return;
            }
            host = args[0];
            accountName = args[1];
            password = args[2];
            charName = args[3];
            if (args.Length >= 5) parseSpell = UInt32.Parse(args[4]);
            if (args.Length >= 6) parseCount = Int32.Parse(args[5]);
            if (args.Length >= 7) parseDelayMs = Int32.Parse(args[6]);
            // "stay" may appear in any position; "auto" is an explicit no-guid placeholder so the
            // target slot can be skipped while still passing later args.
            foreach (string a in args)
                if (a == "stay") stayLoggedIn = true;
            if (args.Length >= 8 && args[7] != "auto" && args[7] != "stay")
                parseTargetGuid = Convert.ToUInt64(args[7], 16);

            System.Console.WriteLine("WotlkBot");
            System.Console.WriteLine("--------");
            System.Console.WriteLine("Trying to connect to " + host);
            System.Console.WriteLine("");

            // Login watchdog. The auth handshake intermittently dies silently on reconnect: the
            // client sends AUTH_LOGON_PROOF, receives a reply, and then simply stops — no callback,
            // no error line, and the process sits in Main's `while (true)` forever looking busy.
            // Without this the only symptom is an empty log, which is easy to misread as "the run
            // produced no damage" rather than "the run never started".
            System.Threading.Thread watchdog = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(45000);
                if (!inWorld)
                {
                    System.Console.WriteLine("PARSE: LOGIN FAILED — never reached the world in 45s "
                        + "(auth handshake stalled; this is intermittent, just run it again)");
                    System.Console.Out.Flush();
                    Environment.Exit(2);
                }
            });
            watchdog.IsBackground = true;
            watchdog.Start();

            LoginCompletedCallBack callback = LoginComplete;

            try
            {
                lclient = new LogonServerClient(host, port, accountName, password, callback);
                lclient.Connect();

            }
            catch (Exception ex)
            {
                System.Console.WriteLine("An error occured: {0}", ex.Message);
            }
            /* just a test to call python for later bot scripting
            string filename = "priest.py";
            string path = Assembly.GetExecutingAssembly().Location;
            string rootDir = Directory.GetParent(path).FullName;

            ScriptEngine engine = Python.CreateEngine();

            ScriptSource source;
            source = engine.CreateScriptSourceFromFile(rootDir + "\\" + filename);

            ScriptScope scope = engine.CreateScope();

            int result = source.ExecuteProgram();
            */

            while (true)
            {

            }
        }

        public static void LoginComplete(uint result)
        {
            if (result == 0)
            {
                RealmListCompletedCallBack callback = RealmListComplete;
                lclient.RequestRealmlist(callback);
            }
            else
                System.Console.WriteLine("Log in falied");
        }

        public static void RealmListComplete(uint result)
        {
            if (result == 0)
            {
                Realm? realm = null;
                System.Console.WriteLine("Realms");
                System.Console.WriteLine("------");
                foreach (Realm r in lclient.Realmlist)
                {
                    System.Console.WriteLine(r.Name);
                    realm = r;
                }
                System.Console.WriteLine("");

                if (realm != null)
                {
                    AuthCompletedCallBack callback = AuthCompleted;
                    wclient = new WorldServerClient(accountName, realm.Value, lclient.mKey, charName, callback);
                    wclient.Connect();
                }
            }
            else
                System.Console.WriteLine("Realmlist failed");
        }

        public static void AuthCompleted(uint result)
        {
            if (result == 0)
            {
                CharEnumCompletedCallBack callback = CharEnumComplete;
                wclient.CharEnumRequest(callback);
            }
            else
                System.Console.WriteLine("Auth failed");
        }

        public static void CharEnumComplete(uint result)
        {
            if(result == 0)
            {
                Character? toLogin = null;
                System.Console.WriteLine("Chars");
                System.Console.WriteLine("-----");
                foreach (Character ch in wclient.Charlist)
                {
                    Console.WriteLine(ch.Name + " (" + ch.Level + ")");
                    if (ch.Name == charName)
                        toLogin = ch;
                }
                System.Console.WriteLine("");

                if (toLogin.HasValue)
                {
                    CharLoginCompletedCallBack callback = CharLoginComplete;
                    wclient.LoginPlayer(toLogin.Value, callback);
                }
            }
            
        }
        public static void CharLoginComplete(uint result)
        {
            if(result == 0)
            {
                inWorld = true;
                System.Console.WriteLine("Logged into world with " + charName);
                System.Threading.Thread t = new System.Threading.Thread(ParseRoutine);
                t.IsBackground = true;
                t.Start();
            }
            else
                System.Console.WriteLine("Char login failed");
        }

        // COA balance harness: after login, find the nearest creature (a training dummy) and
        // cast `parseSpell` `parseCount` times. Every incoming packet is already hex-dumped to
        // the client log, so damage (SMSG_SPELLNONMELEEDAMAGELOG) is parsed from there offline.
        public static void ParseRoutine()
        {
            try
            {
                // POLL for the world to populate; do NOT use a fixed sleep.
                //
                // A fixed 8s was enough for a character that had logged in before, and NOT enough
                // for a FIRST login: the server runs resetSpells() + LearnDefaultSkills() and
                // re-grants the whole CoA ladder before it gets round to streaming nearby units, so
                // at 8s the ObjectMgr held 89 objects and ZERO creatures. ParseRoutine then reported
                // "no creature target found" and shut the bot down BEFORE CommandLoop ever started,
                // silently discarding every queued target/cast command. The log tail showed
                // MONSTER_MOVE lines arriving right after we quit. On the second login the same
                // character saw 125 objects and 35 creatures and worked fine.
                //
                // Every freshly generated roster character is a first login exactly once, so this
                // would have bitten 84 times. Wait for what we actually need — a creature — rather
                // than for a guessed number of seconds.
                const int WAIT_MS = 45000, POLL_MS = 500, MIN_MS = 6000;
                int waited = 0, creatures = 0;
                while (waited < WAIT_MS)
                {
                    System.Threading.Thread.Sleep(POLL_MS);
                    waited += POLL_MS;
                    if (wclient.player == null)
                        continue;
                    creatures = 0;
                    foreach (WotlkClient.Clients.Object o in ObjectMgr.GetInstance().getObjectArray())
                        if (o != null && (o.Guid.GetOldGuid() >> 48) == 0xF130)
                            creatures++;
                    // keep collecting a little after the first creature - they arrive in batches and
                    // the NEAREST one may not be the first to land.
                    if (creatures > 0 && waited >= MIN_MS)
                        break;
                }
                System.Console.WriteLine("PARSE: world populated after " + waited + "ms ("
                    + creatures + " creatures visible)");

                WotlkClient.Clients.Object me = wclient.player;
                if (me == null) { System.Console.WriteLine("PARSE: no player object"); return; }
                // Print our own guid so every DMG line can be attributed to us or to someone else
                // standing at the same dummies. Without this the log is just anonymous damage.
                System.Console.WriteLine("PARSE: me=0x" + me.Guid.GetOldGuid().ToString("X"));

                WotlkClient.Clients.Object[] all = ObjectMgr.GetInstance().getObjectArray();
                System.Console.WriteLine("PARSE: ObjectMgr has " + all.Length + " objects; me @ "
                    + (me.Position != null ? me.Position.X + "," + me.Position.Y : "null"));
                foreach (WotlkClient.Clients.Object dbg in all)
                    System.Console.WriteLine("PARSE:   obj 0x" + dbg.Guid.GetOldGuid().ToString("X")
                        + " hp=" + dbg.Health + " name=" + (dbg.Name ?? "?"));

                // Creature guids seen this session, for diagnosis. A hardcoded target guid is NOT
                // safe: runtime creature guids are reassigned when the world reloads, so a guid
                // lifted from an old combat log silently stops existing and the server DISCARDS
                // every cast aimed at it — no error, no cast animation, nothing in any log.
                System.Console.WriteLine("PARSE: --- creature guids visible now ---");
                foreach (WotlkClient.Clients.Object c in all)
                {
                    if (c == null) continue;
                    UInt64 cg = c.Guid.GetOldGuid();
                    if ((cg >> 48) == 0xF130)
                        System.Console.WriteLine("PARSE:   creature 0x" + cg.ToString("X")
                            + " entry=" + ((cg >> 24) & 0xFFFFFF) + " name=" + (c.Name ?? "?"));
                }

                // Pick the target: an explicit guid if one was supplied, otherwise the nearest
                // creature. Either way we must end up with the target OBJECT, not just its guid —
                // facing needs its position, and a directional spell is refused without it.
                WotlkClient.Clients.Object target = null;
                double best = double.MaxValue;
                foreach (WotlkClient.Clients.Object o in all)
                {
                    if (o == null || o.Position == null || me.Position == null) continue;
                    UInt64 g = o.Guid.GetOldGuid();
                    if (g == me.Guid.GetOldGuid()) continue;

                    if (parseTargetGuid != 0)
                    {
                        if (g != parseTargetGuid) continue;
                        target = o;
                        double ex = o.Position.X - me.Position.X, ey = o.Position.Y - me.Position.Y;
                        best = ex * ex + ey * ey;
                        break;
                    }

                    if ((g >> 48) != 0xF130) continue;      // creatures only (dummies are 0xF130...)
                    // NOTE: do NOT filter on Health here. This client does not parse unit fields,
                    // so EVERY object reports Health == 0 and the old `if (o.Health == 0) continue;`
                    // rejected the entire world — which is why a hardcoded guid was used instead.
                    double dx = o.Position.X - me.Position.X;
                    double dy = o.Position.Y - me.Position.Y;
                    double dz = o.Position.Z - me.Position.Z;
                    double d = dx * dx + dy * dy + dz * dz;
                    if (d < best) { best = d; target = o; }
                }

                if (target == null)
                {
                    System.Console.WriteLine(parseTargetGuid != 0
                        ? "PARSE: explicit target 0x" + parseTargetGuid.ToString("X") + " is NOT in the world "
                          + "— runtime creature guids change every world reload; omit the guid arg to auto-select"
                        : "PARSE: no creature target found");

                    // In `stay` mode DO NOT quit here. This routine's auto-pick failing says nothing
                    // about whether the caller can pick a target later - `stay` exists precisely so
                    // an external driver can send `target entry <n>`. Shutting down instead threw
                    // away every queued command and looked exactly like "the bot ran and measured
                    // nothing".
                    if (!stayLoggedIn)
                    {
                        Shutdown();
                        return;
                    }
                    System.Console.WriteLine("PARSE: READY (no target yet) — staying logged in, "
                        + "polling " + CmdFile + " ; send 'target entry <n>' before 'cast'");
                    CommandLoop(null, 0);
                    return;
                }

                UInt64 tg = target.Guid.GetOldGuid();
                System.Console.WriteLine("PARSE: target=" + (target.Name ?? "?") + " guid=0x" + tg.ToString("X")
                    + " dist=" + Math.Sqrt(best).ToString("F1"));
                System.Console.WriteLine("PARSE: spell=" + parseSpell + " count=" + parseCount + " delayMs=" + parseDelayMs);

                // CMSG_CAST_SPELL carries the target guid explicitly, so no selection/attack
                // needed — and skipping CMSG_ATTACKSWING keeps melee out of the parse.
                if (parseCount > 0)
                    CastBatch(target, tg, parseSpell, parseCount, parseDelayMs);

                if (!stayLoggedIn)
                {
                    Shutdown();
                    return;
                }

                // Persistent mode: keep the session alive and take further batches from a command
                // file. Logging in costs ~8s and logging out ~27s (combat drop + the server's 20s
                // logout timer), so a fresh process per measurement would spend more time on
                // session churn than on measuring. One login, many batches.
                System.Console.WriteLine("PARSE: READY — staying logged in, polling " + CmdFile);
                System.Console.WriteLine("PARSE: commands: 'cast <spellId> <count> <delayMs>' | "
                    + "'target auto|<guidHex>|entry <n>' | 'party <name>...' | 'quit'"
                    + " | 'attack' | 'state' | 'rotate <sec> <id>[:cdMs][:minPowerPct],...'");
                System.Console.WriteLine("PARSE: NOTE for balance runs use 'target entry 31146' "
                    + "(Heroic Training Dummy, LEVEL 83 boss-rank). Auto-target picks the nearest, "
                    + "which at the Stormwind dummies is entry 32666 at LEVEL 60 — a no-miss target "
                    + "that does not match the model's level-83 assumption.");
                CommandLoop(target, tg);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine("PARSE error: " + ex.Message);
                Shutdown();
            }
        }

        // Cast one measurement batch, re-facing the target before each cast.
        private static void CastBatch(WotlkClient.Clients.Object target, UInt64 tg,
                                      uint spell, int count, int delayMs)
        {
            uint t0 = (uint)Environment.TickCount;
            for (int i = 0; i < count; i++)
            {
                // Re-face every iteration: cheap, and it keeps working if the bot ever moves.
                wclient.FaceTarget(target.Position.X, target.Position.Y,
                                   (uint)Environment.TickCount - t0);
                System.Threading.Thread.Sleep(150);        // let the server apply the new facing
                wclient.CastSpell(tg, spell);
                System.Console.WriteLine("PARSE: cast " + (i + 1) + "/" + count + " spell=" + spell);
                System.Threading.Thread.Sleep(delayMs);
            }
            System.Console.WriteLine("PARSE: BATCH DONE spell=" + spell);
        }

        // PRIORITY-LIST ROTATION (bot milestone M3).
        //
        // The old CastBatch fires one spell N times on a fixed timer with no idea whether it is off
        // cooldown or affordable. Against real class resources that produced 312 cast failures in
        // the FP-06 sweep - dominated by 85 NO_POWER and 67 NOT_READY - and no amount of tuning the
        // CALLER could fix it, because the decision has to be made here, with the bot's own state.
        //
        // Spec: "<id>[:<cdMs>][:<minPowerPct>],<id>..." highest priority first.
        //   * skips an entry still on its own cooldown
        //   * skips an entry when the relevant power pool is below its threshold
        //   * on 85 NO_POWER, backs off that entry for 3s instead of hammering it
        //   * on 67 NOT_READY, learns the cooldown it did not know about
        private static void Rotate(WotlkClient.Clients.Object target, UInt64 tg, string spec, int seconds)
        {
            string[] entries = spec.Split(',');
            uint[] ids = new uint[entries.Length];
            int[] cds = new int[entries.Length];
            int[] minPct = new int[entries.Length];
            var readyAt = new System.Collections.Generic.Dictionary<uint, System.DateTime>();
            for (int i = 0; i < entries.Length; i++)
            {
                string[] f = entries[i].Split(':');
                ids[i] = UInt32.Parse(f[0]);
                cds[i] = f.Length > 1 ? Int32.Parse(f[1]) : 1500;
                minPct[i] = f.Length > 2 ? Int32.Parse(f[2]) : 0;
                readyAt[ids[i]] = System.DateTime.MinValue;
            }
            System.Console.WriteLine("PARSE: ROTATE " + seconds + "s over " + ids.Length
                + " spell(s); " + wclient.DescribeState());

            System.DateTime end = System.DateTime.Now.AddSeconds(seconds);
            uint t0 = (uint)Environment.TickCount;
            int cast = 0, skippedCd = 0, skippedPower = 0;
            while (System.DateTime.Now < end)
            {
                bool did = false;
                for (int i = 0; i < ids.Length; i++)
                {
                    if (System.DateTime.Now < readyAt[ids[i]]) { skippedCd++; continue; }
                    if (minPct[i] > 0)
                    {
                        // pick whichever pool this character actually uses
                        int best = 0;
                        for (byte pt = 0; pt < 7; pt++)
                            if (wclient.MaxPower[pt] > 0 && wclient.PowerPct(pt) > best)
                                best = wclient.PowerPct(pt);
                        if (best < minPct[i]) { skippedPower++; continue; }
                    }
                    // STOP IF WE ARE DEAD. A corpse cannot cast; continuing produces a run of
                    // failures that reads exactly like "this branch deals no damage", which is a
                    // BALANCE conclusion drawn from a death. Break out and say so - the sweep
                    // treats a death as a suppressing precondition failure (task #72).
                    if (wclient.IsDead)
                    {
                        System.Console.WriteLine("PARSE: rotation ABORTED - character is DEAD"
                            + " (cast " + cast + " before dying)");
                        break;
                    }
                    wclient.FaceTarget(target.Position.X, target.Position.Y,
                                       (uint)Environment.TickCount - t0);
                    System.Threading.Thread.Sleep(120);
                    wclient.LastFailedSpell = 0;
                    wclient.CastSpell(tg, ids[i]);
                    cast++;
                    // EMIT THE SAME MARKER CastBatch DOES. The sweep parser attributes damage by
                    // CAST WINDOW - it tracks the most recent "PARSE: cast n/m spell=X" line and
                    // credits following DMG lines to it. Without this, a rotation casts perfectly
                    // and the parser sees NOTHING, which is exactly what happened in sweep v6:
                    // every branch reported "only 0 non-crit payoff hits" while the bots were in
                    // fact casting the whole time.
                    System.Console.WriteLine("PARSE: cast " + cast + "/" + cast + " spell=" + ids[i]);
                    readyAt[ids[i]] = System.DateTime.Now.AddMilliseconds(cds[i]);
                    System.Threading.Thread.Sleep(1600);          // global cooldown
                    if (wclient.LastFailedSpell == ids[i])
                    {
                        if (wclient.LastFailedCode == 85)         // NO_POWER - let it regenerate
                            readyAt[ids[i]] = System.DateTime.Now.AddSeconds(3);
                        else if (wclient.LastFailedCode == 67)    // NOT_READY - learn the real cd
                            readyAt[ids[i]] = System.DateTime.Now.AddMilliseconds(Math.Max(cds[i], 3000));
                    }
                    did = true;
                    break;
                }
                if (!did)
                    System.Threading.Thread.Sleep(400);           // everything gated; wait it out
            }
            System.Console.WriteLine("PARSE: ROTATE DONE cast=" + cast + " skippedCooldown="
                + skippedCd + " skippedPower=" + skippedPower + "; " + wclient.DescribeState());
        }

        // Poll the command file so batches can be driven from outside without relogging.
        // The file is truncated as soon as it is read, so writing to it is the whole protocol.
        private static void CommandLoop(WotlkClient.Clients.Object target, UInt64 tg)
        {
            while (true)
            {
                string line = null;
                try
                {
                    if (File.Exists(CmdFile))
                    {
                        line = File.ReadAllText(CmdFile).Trim();
                        File.WriteAllText(CmdFile, "");
                    }
                }
                catch (IOException) { /* writer still has it open; pick it up next tick */ }

                if (string.IsNullOrEmpty(line)) { System.Threading.Thread.Sleep(500); continue; }

                string[] a = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                try
                {
                    if (a[0] == "quit") { Shutdown(); return; }

                    if (a[0] == "cast")
                    {
                        // CommandLoop can now be entered with no target at all (see ParseRoutine),
                        // and CastBatch dereferences target.Position to face it. Say so plainly
                        // rather than throwing a NullReference into the command handler, which
                        // would read as "the cast failed" instead of "you never aimed".
                        if (target == null)
                        {
                            System.Console.WriteLine("PARSE: no target selected — send "
                                + "'target entry <n>' (e.g. 31146) before 'cast'");
                            continue;
                        }
                        // TASK #81 — optional RANGE BAND: `cast <id> <n> <delayMs> [min] [max]`.
                        // Without it the bot casts from wherever it stands, which at a dummy is
                        // point-blank, and every RANGED ability is refused with 128 TOO_CLOSE.
                        // A refusal is not a measurement, so three whole 21-branch sweeps recorded
                        // Ranger as "NO BUILD" when its engine was fine and merely ranged.
                        // The caller supplies the band because Python already reads SpellRange.dbc
                        // (minHostile/maxHostile) — the bot deliberately parses no DBCs.
                        if (a.Length > 4)
                        {
                            float rmin = Single.Parse(a[4], System.Globalization.CultureInfo.InvariantCulture);
                            float rmax = a.Length > 5
                                ? Single.Parse(a[5], System.Globalization.CultureInfo.InvariantCulture)
                                : rmin + 20.0f;
                            if (!wclient.MoveToRange(target, rmin, rmax))
                                System.Console.WriteLine("PARSE: could not reach the range band — "
                                    + "casting anyway, but treat a refusal as POSITIONING, not a "
                                    + "dead spell");
                        }
                        CastBatch(target, tg, UInt32.Parse(a[1]),
                                  a.Length > 2 ? Int32.Parse(a[2]) : 8,
                                  a.Length > 3 ? Int32.Parse(a[3]) : 6500);
                    }
                    else if (a[0] == "moveto")
                    {
                        // `moveto <min> [max]` — position without casting, so a sweep can set up
                        // once and then fire several batches, and so the band can be tested on
                        // its own when a cast still fails.
                        if (target == null) { System.Console.WriteLine("PARSE: no target"); continue; }
                        float mn = Single.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
                        float mx = a.Length > 2
                            ? Single.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture)
                            : mn + 20.0f;
                        System.Console.WriteLine("PARSE: moveto " + (wclient.MoveToRange(target, mn, mx)
                            ? "IN BAND" : "FAILED"));
                    }
                    else if (a[0] == "attack")
                    {
                        // AUTOATTACK. Program.cs used to skip CMSG_ATTACKSWING deliberately "to
                        // keep melee out of the parse" - but that means RAGE and ENERGY classes
                        // never generate a resource at all, so every one of their casts returns
                        // 85 SPELL_FAILED_NO_POWER. A Warrior starts at 0 rage. That single
                        // omission is why 312 casts failed across the FP-06 sweep.
                        if (target == null)
                            System.Console.WriteLine("PARSE: no target selected");
                        else
                        {
                            wclient.Attack(target);
                            System.Console.WriteLine("PARSE: autoattacking - resource classes can "
                                + "now build rage/energy");
                        }
                    }
                    else if (a[0] == "state")
                    {
                        System.Console.WriteLine("PARSE: state " + wclient.DescribeState());
                    }
                    else if (a[0] == "clearaura")
                    {
                        // `clearaura <spellId> [...]` - empty an engine's stack BEFORE measuring it.
                        // Required because a non-decaying counter is already at its cap by the time
                        // the baseline runs (see WorldServerClient.CancelAura), which makes the
                        // baseline-vs-charged comparison meaningless: Stormbringer's Static sat at
                        // 100/100 through both halves of every sweep.
                        // Reports the stack level before and after, so the caller can distinguish a
                        // successful removal from an aura the server refused to cancel.
                        for (int ai = 1; ai < a.Length; ai++)
                        {
                            uint sid;
                            if (!uint.TryParse(a[ai], out sid))
                                continue;
                            int before = wclient.AuraStacks(sid);
                            wclient.CancelAura(sid);
                            System.Threading.Thread.Sleep(400);
                            int after = wclient.AuraStacks(sid);
                            System.Console.WriteLine("PARSE: clearaura spell=" + sid
                                + " before=" + before + " after=" + after
                                + (after == 0 ? " CLEARED" : " !! STILL PRESENT"));
                        }
                    }
                    else if (a[0] == "rotate")
                    {
                        // rotate <seconds> <spellId>[:<cooldownMs>][:<minPowerPct>] , ...
                        // A PRIORITY LIST, not a spam loop: cast the first entry that is off
                        // cooldown and affordable, then re-evaluate. This is what the sweep needed
                        // and never had.
                        if (target == null) { System.Console.WriteLine("PARSE: no target"); continue; }
                        int secs = Int32.Parse(a[1]);
                        Rotate(target, tg, a[2], secs);
                    }
                    else if (a[0] == "party")
                    {
                        // "party Healbot Dpsbot ..." — the squad leader invites the others, who
                        // auto-accept. A real party is required for group-only heals, party/raid
                        // buffs, and any threat test where the tank must hold aggro off the
                        // healer rather than off nobody.
                        for (int i = 1; i < a.Length; i++)
                        {
                            wclient.InviteToGroup(a[i]);
                            System.Threading.Thread.Sleep(400);
                        }
                    }
                    else if (a[0] == "target")
                    {
                        // "target auto" | "target <guidHex>" | "target entry <n>"
                        //   | "target player"  — the nearest PLAYER, i.e. another bot.
                        // The player form exists for M4: a healer cannot heal what it cannot
                        // select, and until now `target` resolved creatures only.
                        WotlkClient.Clients.Object nt;
                        if (a[1] == "entry")
                            nt = ResolveTarget(0, UInt32.Parse(a[2]));
                        else if (a[1] == "player")
                            nt = ResolveTarget(0, 0, true);
                        else
                            nt = ResolveTarget(a[1] == "auto" ? 0 : Convert.ToUInt64(a[1], 16));
                        if (nt == null) System.Console.WriteLine("PARSE: target not found: " + a[1]);
                        else
                        {
                            target = nt; tg = nt.Guid.GetOldGuid();
                            System.Console.WriteLine("PARSE: target now 0x" + tg.ToString("X"));
                        }
                    }
                    else System.Console.WriteLine("PARSE: unknown command: " + line);
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine("PARSE: command '" + line + "' failed: " + ex.Message);
                }
            }
        }

        // Nearest creature (wantGuid == 0), one specific guid, or the nearest creature of a given
        // creature_template entry (wantEntry != 0).
        //
        // TARGET BY ENTRY IS THE ONE TO USE. Runtime guids are regenerated every world reload, so
        // a hardcoded guid rots immediately; and "nearest creature" silently picked the wrong
        // thing — at the Stormwind dummies the nearest is entry 32666 "Expert's Training Dummy",
        // which is LEVEL 60. Every measurement taken against it was a no-miss measurement, while
        // the balance model assumes a level 83 boss (17% base miss for a level 80 caster before
        // hit rating). Entry 31146 "Heroic Training Dummy" is level 83 / rank 3 and is already
        // spawned there — that is the correct DPS target.
        //
        // A creature guid encodes its entry: 0xF130 << 48 | entry << 24 | counter, so the entry
        // is stable across restarts even though the counter is not.
        private static WotlkClient.Clients.Object ResolveTarget(UInt64 wantGuid, uint wantEntry = 0,
                                                               bool wantPlayer = false)
        {
            WotlkClient.Clients.Object me = wclient.player;
            if (me == null || me.Position == null) return null;

            WotlkClient.Clients.Object found = null;
            double best = double.MaxValue;
            foreach (WotlkClient.Clients.Object o in ObjectMgr.GetInstance().getObjectArray())
            {
                if (o == null || o.Position == null) continue;
                UInt64 g = o.Guid.GetOldGuid();
                if (g == me.Guid.GetOldGuid()) continue;

                if (wantGuid != 0) { if (g == wantGuid) return o; continue; }

                // FRIENDLY TARGETING. Every scan here filtered on (guid>>48)==0xF130, i.e. creatures
                // only - so the bot could see other PLAYERS and threw them away, and a healer bot
                // literally could not select the bot it was meant to heal. That is the single gap
                // blocking every healing measurement (M4), and it also blocks threat tests, where
                // the tank and the healer have to be able to see each other.
                // A player guid has a zero high word rather than the 0xF130 creature marker.
                if (wantPlayer)
                {
                    if ((g >> 48) == 0xF130) continue;      // that is a creature, not a player
                }
                else
                {
                    if ((g >> 48) != 0xF130) continue;
                    if (wantEntry != 0 && ((g >> 24) & 0xFFFFFF) != wantEntry) continue;
                }

                double dx = o.Position.X - me.Position.X, dy = o.Position.Y - me.Position.Y,
                       dz = o.Position.Z - me.Position.Z;
                double d = dx * dx + dy * dy + dz * dz;
                if (d < best) { best = d; found = o; }
            }
            return found;
        }

        // Leave the world cleanly. A hard `taskkill` sends no logout packet, so the server holds
        // the session open and the character stays visible in-world and flagged `online=1` in the
        // characters table. The next run then logs into a half-alive session whose target lookups
        // fail — every cast comes back SPELL_FAILED_BAD_TARGETS (code 12) with no other symptom.
        // That ghost session, not any balance change, is what silently kills the harness.
        private static void Shutdown()
        {
            System.Console.WriteLine("PARSE: DONE");
            try
            {
                // Combat blocks logout outright, and attacking a training dummy always leaves the
                // bot in combat. Wait out the ~5s combat drop first, then request logout and wait
                // for the server's ~20s timer to actually fire.
                System.Threading.Thread.Sleep(7000);
                wclient.Logout();

                for (int i = 0; i < 260 && !wclient.LogoutComplete; i++)
                {
                    System.Threading.Thread.Sleep(100);
                    if (wclient.LogoutDenied > 0 && i == 20)
                    {
                        System.Console.WriteLine("PARSE: retrying logout after denial");
                        wclient.Logout();
                    }
                }
                if (!wclient.LogoutComplete)
                    System.Console.WriteLine("PARSE: WARNING logout never completed — " + charName
                        + " may linger in-world; clear with "
                        + "UPDATE characters SET online=0 WHERE name='" + charName + "'");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine("PARSE: logout failed: " + ex.Message);
            }
            System.Console.Out.Flush();
            Environment.Exit(0);                       // break Main's `while (true)` spin
        }
    }
}
