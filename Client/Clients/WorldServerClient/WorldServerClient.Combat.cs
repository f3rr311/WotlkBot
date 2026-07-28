using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using WotlkClient.Shared;
using WotlkClient.Network;
using WotlkClient.Crypt;
using WotlkClient.Constants;

namespace WotlkClient.Clients
{
    public partial class WorldServerClient
    {
        public void Attack(Object target)
        {
            PacketOut packet = new PacketOut(WorldServerOpCode.CMSG_SET_SELECTION);
            if (player != null)
            {
                packet.Write(target.Guid.GetNewGuid());
            }
            Send(packet);

            packet = new PacketOut(WorldServerOpCode.CMSG_ATTACKSWING);
            if (player != null)
            {
                packet.Write(target.Guid.GetNewGuid());
            }
            Send(packet);
        }

        /// <summary>
        /// Drop one of our own auras. THIS IS A MEASUREMENT PREREQUISITE, not a convenience.
        ///
        /// A stacking engine counter with no duration and no decay (Stormbringer's 803102 Static is
        /// CumulativeAura 100 and infinite) is left FULL by anything earlier in the login - the
        /// buff phase, the autoattack warm-up, a previous branch on the same character. The FP-06
        /// sweep then measured a "baseline" with the engine already at its cap AND a "charged" half
        /// at the same cap, so the delta came out ~0 BY CONSTRUCTION however strong the engine was.
        /// Static was observed at 100/100 two minutes before its builder was ever cast.
        ///
        /// Clearing the stack before the baseline is what makes the comparison mean anything.
        /// The server only honours this for auras the client is permitted to cancel - a passive, or
        /// one flagged not-cancellable, simply stays - so the sweep must still VERIFY the level
        /// afterwards rather than assume the removal worked.
        /// </summary>
        public void CancelAura(uint spellId)
        {
            PacketOut packet = new PacketOut(WorldServerOpCode.CMSG_CANCEL_AURA);
            packet.Write(spellId);
            Send(packet);
        }
    }
}
