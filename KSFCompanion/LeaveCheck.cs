using System;

namespace KsfCompanion
{
    /// <summary>
    /// Whether you're still on a server, after a hint that you may have left it: the live demo stopped (it does when you
    /// leave, and when you switch servers - "Connecting to ..." then follows straight away), ksf.surf's list lost you, or
    /// the console has been quiet for long. The game is asked for "status", which goes to the server you're on: it
    /// answers with its address. Off a server nothing comes back at all (the game doesn't say it isn't connected), so
    /// two unanswered asks in a row mean you left - as of the hint.
    /// </summary>
    sealed class LeaveCheck
    {
        /// <summary>How long a server gets to answer.</summary>
        public static readonly TimeSpan AnswerTime = TimeSpan.FromSeconds(6);

        DateTime? askAt, askedAt;
        DateTime hintAt, answeredAt = DateTime.MinValue;
        int unanswered;

        /// <summary>A check is under way.</summary>
        public bool Pending => askAt != null || askedAt != null;

        /// <summary>You may have left at <paramref name="at"/>: the game gets asked after <paramref name="delay"/>.</summary>
        public void Hint(DateTime at, TimeSpan delay, DateTime now)
        {
            if (!Pending)
            {
                hintAt = at;
                unanswered = 0;
            }
            var ask = now + delay;
            if (askAt == null || ask < askAt) askAt = ask;
        }

        /// <summary>A server answered "status" (its udp/ip line came).</summary>
        public void Answered(DateTime now) => answeredAt = now;

        /// <summary>Joining a server, or plainly on one: nothing to check.</summary>
        public void Cancel()
        {
            askAt = askedAt = null;
            unanswered = 0;
        }

        /// <summary>Every tick: whether to ask the game now ("status"), or since when you've been off the server.</summary>
        public (bool Ask, DateTime? LeftAt) Tick(DateTime now)
        {
            if (askedAt is DateTime asked && now - asked > AnswerTime)
            {
                askedAt = null;
                if (answeredAt >= asked) unanswered = 0;
                else if (++unanswered >= 2)
                {
                    var since = hintAt;
                    Cancel();
                    return (false, since);
                }
                // Once more, to be sure.
                else askAt = now;
            }
            if (askAt is DateTime ask && now >= ask && askedAt == null)
            {
                askAt = null;
                askedAt = now;
                return (true, null);
            }
            return (false, null);
        }
    }
}
