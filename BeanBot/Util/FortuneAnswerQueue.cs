namespace BeanBot.Util
{
    public readonly record struct FortuneAnswerReservation(ulong RecipientId, string Answer, long Version);

    public sealed class FortuneAnswerQueue
    {
        private readonly object _sync = new object();
        private string _answer;
        private ulong _recipientId;
        private long _version;

        public void Queue(ulong recipientId, bool positive)
        {
            lock (_sync)
            {
                _recipientId = recipientId;
                _answer = positive ? "positive" : "negative";
                _version++;
            }
        }

        public bool TryReserve(ulong recipientId, out FortuneAnswerReservation reservation)
        {
            lock (_sync)
            {
                if (_answer == null || recipientId != _recipientId)
                {
                    reservation = default;
                    return false;
                }

                reservation = new FortuneAnswerReservation(_recipientId, _answer, _version);
                return true;
            }
        }

        public bool Consume(FortuneAnswerReservation reservation)
        {
            lock (_sync)
            {
                if (_answer != reservation.Answer ||
                    _recipientId != reservation.RecipientId ||
                    _version != reservation.Version)
                {
                    return false;
                }

                _answer = null;
                _recipientId = 0;
                return true;
            }
        }
    }
}
