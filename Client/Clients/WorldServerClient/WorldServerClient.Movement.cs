using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Timers;

using System.Runtime.InteropServices;
using System.Resources;

using WotlkClient.Shared;
using WotlkClient.Network;
using WotlkClient.Constants;

namespace WotlkClient.Clients
{
    public partial class WorldServerClient
    {
        // forward flag   flag2    time            pos.x       pos.y           pos.z       pos.o
        // 01 00 00 00 - 00 00 - 48 36 5f 17 - 13 82 0b c6 - 1c a3 fe c2 - d1 c8 a3 42 - 3d d8 93 40 - 00 00 00 00
        // stop
        // 00 00 00 00 - 00 00 - 14 38 5f 17 - 43 83 0b c6 - 5f 86 02 c3 - 80 25 a4 42 - 3d d8 93 40 - 00 00 00 00
        // 01 = MOVEMENTFLAG_FORWARD
        // 00 = MOVEMENTFLAG_NONE
        public void MoveForward(Coordinate position, uint time)
        {
            PacketOut packet = new PacketOut(WorldServerOpCode.MSG_MOVE_START_FORWARD);
            AppendPackedGuid(player.Guid.GetOldGuid(), packet);
            packet.Write((UInt32)0);    // flags
            packet.Write((UInt16)0);    // flags2
            packet.Write((UInt32)time);
            packet.Write(position.X);
            packet.Write(position.Y);
            packet.Write(position.Z);
            packet.Write(position.O);
            packet.Write((UInt32)0);    // falltime  
            Send(packet);
            Console.WriteLine("move forward " + player.Name + ", " + position.ToString());
        }

        // Turn to face a world point. Directional spells fail with SPELL_FAILED_UNIT_NOT_INFRONT
        // (code 134) unless the server thinks the caster is looking at the target — a headless bot
        // has no camera, so its orientation stays at whatever it logged in with and every cast is
        // silently refused. Same movement block as MoveStop, only the orientation differs.
        public void FaceTarget(float targetX, float targetY, uint time)
        {
            if (player == null || player.Position == null)
                return;

            float o = (float)Math.Atan2(targetY - player.Position.Y, targetX - player.Position.X);
            if (o < 0) o += (float)(2 * Math.PI);          // server expects orientation in [0, 2pi)
            player.Position.O = o;

            PacketOut packet = new PacketOut(WorldServerOpCode.MSG_MOVE_SET_FACING);
            AppendPackedGuid(player.Guid.GetOldGuid(), packet);
            packet.Write((UInt32)0);    // movement flags — standing still
            packet.Write((UInt16)0);    // flags2
            packet.Write((UInt32)time);
            packet.Write(player.Position.X);
            packet.Write(player.Position.Y);
            packet.Write(player.Position.Z);
            packet.Write(o);
            packet.Write((UInt32)0);    // falltime
            Send(packet);
        }

        /// <summary>
        /// Walk until the caster is inside [minRange, maxRange] of <paramref name="target"/>.
        /// Returns true if the band was reached (or we were already inside it).
        /// </summary>
        /// <remarks>
        /// TASK #81 — THE BOT COULD NOT CAST RANGED SPELLS AT ALL.
        ///
        /// It auto-targets the NEAREST creature and casts from wherever it happens to stand,
        /// which at a training dummy is point-blank. Every ranged ability is then refused
        /// client-side with SPELL_FAILED_TOO_CLOSE (128) — and a refusal is not a measurement.
        /// This made EVERY null result on a ranged branch meaningless: Ranger was recorded as
        /// "NO BUILD — tried every declared generator" through three whole 21-branch sweeps
        /// when nothing was wrong with its engine at all. Its generators are simply ranged.
        ///
        /// There is no single distance that works. Measured 2026-07-27: 128 TOO_CLOSE at
        /// 2.5 yd, 97 OUT_OF_RANGE at 12–30 yd, and ~7 yd failed BOTH on different spells.
        /// So the band has to come from the spell, and the caller passes it in — Python reads
        /// SpellRange.dbc (minHostile/maxHostile) and hands it over on the `cast` line. The bot
        /// deliberately does NOT parse DBCs; the measurement layer already has that data.
        ///
        /// Movement is client-authoritative in 3.3.5, so we could simply assert the destination.
        /// We walk it instead: START_FORWARD, sleep the real travel time at run speed, then STOP
        /// at the destination. An instant relocation is exactly what an anticheat module flags,
        /// and this server runs one.
        /// </remarks>
        /// <param name="bearingDeg">
        /// Rotate the destination this many degrees around the target instead of retreating along
        /// the straight line away from it.
        ///
        /// MEASURED 2026-07-28: backing straight off traded 128 TOO_CLOSE for
        /// 47 SPELL_FAILED_LINE_OF_SIGHT — the retreat walked the bot behind scenery. Repositioning
        /// is not a scalar; a distance alone can be correct and still be unusable. The caller
        /// retries a few bearings because only it can see the cast result.
        /// </param>
        public bool MoveToRange(Object target, float minRange, float maxRange, int timeoutMs = 8000,
                                float bearingDeg = 0.0f)
        {
            if (player == null || player.Position == null || target == null || target.Position == null)
            {
                Console.WriteLine("RANGE: no position for player or target");
                return false;
            }
            if (maxRange <= 0 || maxRange < minRange)
            {
                Console.WriteLine("RANGE: bad band min=" + minRange + " max=" + maxRange);
                return false;
            }

            float dx = player.Position.X - target.Position.X;
            float dy = player.Position.Y - target.Position.Y;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);

            // ⚠ ELEVATION IS PART OF BEING IN POSITION, AND `dist` DOES NOT CONTAIN IT.
            //
            // `dist` above is HORIZONTAL. This early-return used to test it alone, and that single
            // omission cost three test rounds: the bot reported "already at 12.0 yd" and refused to
            // move while standing 4.4 yd BELOW the dummy's platform, shooting at the ledge and
            // failing 47 LINE_OF_SIGHT every time. Distance is a scalar; POSITION IS NOT — the same
            // mistake as ignoring the bearing, one field further down.
            float dzNow = player.Position.Z - target.Position.Z;
            bool wrongLevel = Math.Abs(dzNow) > 2.0f;
            bool arcing = Math.Abs(bearingDeg) > 0.01f;

            // Aim for the middle of the band, not its edge: the target may drift, and floating
            // point on the server side is not going to agree with ours to the last yard.
            float want;
            if (dist < minRange) want = Math.Min(minRange + 3.0f, (minRange + maxRange) / 2.0f);
            else if (dist > maxRange) want = Math.Max(maxRange - 3.0f, (minRange + maxRange) / 2.0f);
            else if (arcing || wrongLevel)
            {
                // ⚠ DO NOT SHORT-CIRCUIT ON DISTANCE WHEN A BEARING OR A LEVEL CHANGE IS NEEDED.
                //
                // The first version returned early whenever the distance was legal, which made the
                // bearing a no-op: the caller's line-of-sight retry re-cast from the identical
                // blocked spot at 0, +45, -45 and +90 degrees and logged "already at 12.0 yd" every
                // time. Four different bearings, one position. A bearing is a request for a
                // DIFFERENT PLACE, not a different distance.
                want = dist;                                   // keep the distance, change the spot
            }
            else
            {
                Console.WriteLine("RANGE: already at " + dist.ToString("F1")
                    + " yd, band [" + minRange.ToString("F1") + "," + maxRange.ToString("F1")
                    + "], dz " + dzNow.ToString("+0.0;-0.0") + " — in position");
                return true;
            }

            // Degenerate case: standing exactly on the target gives no direction to back off in.
            // Pick one rather than dividing by zero.
            if (dist < 0.01f) { dx = 1.0f; dy = 0.0f; dist = 1.0f; }

            float ux = dx / dist, uy = dy / dist;              // unit vector target -> player
            if (Math.Abs(bearingDeg) > 0.01f)
            {
                // Swing the destination around the target. Same distance, different side — which
                // is what a line-of-sight failure actually needs.
                double rad = bearingDeg * Math.PI / 180.0;
                double c = Math.Cos(rad), s = Math.Sin(rad);
                float rx = (float)(ux * c - uy * s);
                float ry = (float)(ux * s + uy * c);
                ux = rx; uy = ry;
            }
            float destX = target.Position.X + ux * want;
            float destY = target.Position.Y + uy * want;
            // ⚠ THE TARGET'S Z, NOT OURS. The comment here used to read "dummies are on flat
            // ground" — they are not. The Stormwind heroic dummy stands at z=103.0 on a RAISED
            // PLATFORM, so carrying our own Z forward meant backing 7.5 yd off walked the bot DOWN
            // to z=98.6, off the edge, and every cast after that returned 47 LINE_OF_SIGHT.
            // Taking the target's Z keeps us on whatever surface the target is standing on.
            float destZ = target.Position.Z;

            // Travel distance is the CHORD, not the difference of radii. When arcing, `want` equals
            // `dist` and the radial difference is zero — the old formula then slept ~150 ms and
            // stopped the bot before it had gone anywhere.
            float travel = (float)Math.Sqrt((destX - player.Position.X) * (destX - player.Position.X)
                                          + (destY - player.Position.Y) * (destY - player.Position.Y));

            const float RUN_SPEED = 7.0f;                      // yards/sec, WotLK default
            int travelMs = (int)(travel / RUN_SPEED * 1000.0f) + 150;
            if (travelMs > timeoutMs) travelMs = timeoutMs;

            Console.WriteLine("RANGE: at " + dist.ToString("F1") + " yd, want "
                + want.ToString("F1") + " (band [" + minRange.ToString("F1") + ","
                + maxRange.ToString("F1") + "]) — walking " + travel.ToString("F1")
                + " yd over " + travelMs + " ms");

            // Face the way we are travelling, or the server sees us sliding sideways.
            FaceTarget(destX, destY, (uint)Environment.TickCount);
            MoveForward(player.Position, (uint)Environment.TickCount);
            System.Threading.Thread.Sleep(travelMs);

            Coordinate dest = new Coordinate(destX, destY, destZ, player.Position.O);
            MoveStop(dest, (uint)Environment.TickCount);

            // MoveForward/MoveStop only SEND a position — they do not update ours. Without this
            // the next FaceTarget computes its angle from the pre-move position and aims wrong.
            player.Position.X = destX;
            player.Position.Y = destY;
            player.Position.Z = destZ;      // or the next wrongLevel test compares against a stale Z

            // Turn back to the target: we just faced our direction of travel, and backing off
            // means we are now looking AWAY from it. A directional spell would be refused with
            // SPELL_FAILED_UNIT_NOT_INFRONT (134) — a different silent refusal.
            FaceTarget(target.Position.X, target.Position.Y, (uint)Environment.TickCount);
            System.Threading.Thread.Sleep(250);               // let the server settle the move

            float nx = player.Position.X - target.Position.X;
            float ny = player.Position.Y - target.Position.Y;
            float now = (float)Math.Sqrt(nx * nx + ny * ny);
            float dzEnd = player.Position.Z - target.Position.Z;
            bool ok = now >= minRange && now <= maxRange && Math.Abs(dzEnd) <= 2.0f;
            Console.WriteLine("RANGE: now " + now.ToString("F1") + " yd, dz "
                + dzEnd.ToString("+0.0;-0.0") + " — " + (ok ? "IN POSITION" : "STILL OUT"));
            return ok;
        }

        public void MoveStop(Coordinate position, uint time)
        {
            PacketOut packet = new PacketOut(WorldServerOpCode.MSG_MOVE_STOP);
            AppendPackedGuid(player.Guid.GetOldGuid(), packet);
            packet.Write((UInt32)0);    // flags
            packet.Write((UInt16)0);    // flags2
            packet.Write((UInt32)time);
            packet.Write(position.X);
            packet.Write(position.Y);
            packet.Write(position.Z);
            packet.Write(position.O);
            packet.Write((UInt32)0);    // falltime 
            Send(packet);
            Console.WriteLine("move stop " + player.Name);
        }


        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_START_FORWARD)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_START_BACKWARD)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_STOP)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_START_STRAFE_LEFT)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_START_STRAFE_RIGHT)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_STOP_STRAFE)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_JUMP)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_START_TURN_LEFT)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_START_TURN_RIGHT)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_STOP_TURN)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_START_PITCH_UP)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_START_PITCH_DOWN)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_STOP_PITCH)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_SET_RUN_MODE)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_SET_WALK_MODE)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_TOGGLE_LOGGING)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_TELEPORT)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_TELEPORT_CHEAT)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_TELEPORT_ACK)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_TOGGLE_FALL_LOGGING)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_FALL_LAND)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_START_SWIM)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_STOP_SWIM)]
        [PacketHandlerAtribute(WorldServerOpCode.MSG_MOVE_HEARTBEAT)]
        public void HandleAnyMove(PacketIn packet)
        {
            Object obj;
            WoWGuid updateGuid = new WoWGuid(UnpackGuid(packet));
            if (ObjectMgr.GetInstance().objectExists(updateGuid))
            {
                obj = ObjectMgr.GetInstance().getObject(updateGuid);
            }
            else
            {
                obj = new Object(updateGuid);
                ObjectMgr.GetInstance().addObject(obj);
            }
            ReadMovementInfoLegacy(packet, obj);
            /*
            byte mask = packet.ReadByte();

            WoWGuid guid = new WoWGuid(mask, packet.ReadBytes(WoWGuid.BitCount8(mask)));

            Object obj = ObjectMgr.GetInstance().getObject(guid);
            if (obj != null)
            {
                    packet.ReadBytes(9);
                    obj.Position= new Coordinate(packet.ReadFloat(), packet.ReadFloat(), packet.ReadFloat(), packet.ReadFloat());
            }
            */
        }

        [PacketHandlerAtribute(WorldServerOpCode.SMSG_MONSTER_MOVE)]
        public void HandleMonsterMove(PacketIn packet)
        {
            byte mask = packet.ReadByte();

            WoWGuid guid = new WoWGuid(mask, packet.ReadBytes(WoWGuid.BitCount8(mask)));

            Object obj = ObjectMgr.GetInstance().getObject(guid);
            if (obj != null)
            {
                System.Console.WriteLine("MONSTER_MOVE " + obj.Name);
                obj.Position = new Coordinate(packet.ReadFloat(), packet.ReadFloat(), packet.ReadFloat());
            }
            else
            {
                QueryName(guid);
            }
        }

        void Heartbeat(object source, ElapsedEventArgs e)
        {
            if (player == null || player.Position == null)
                return;

            PacketOut packet = new PacketOut(WorldServerOpCode.MSG_MOVE_HEARTBEAT);
            packet.Write(movementMgr.Flag.MoveFlags);
            packet.Write((UInt16)0);
            packet.Write((UInt32)MM_GetTime());
            packet.Write((float)player.Position.X);
            packet.Write((float)player.Position.Y);
            packet.Write((float)player.Position.Z);
            packet.Write((float)player.Position.O);
            packet.Write((UInt32)0);
            Send(packet);
        }
    }
}

