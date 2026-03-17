namespace BeanBot.Services;

public sealed class EightBallQueueService
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ulong, string> _queuedAnswers = [];

    public void Queue(string answer, ulong recipientId)
    {
        lock (_lock)
        {
            _queuedAnswers[recipientId] = answer;
        }
    }

    public string? TryDequeue(ulong recipientId)
    {
        lock (_lock)
        {
            if (!_queuedAnswers.Remove(recipientId, out var queuedAnswer))
            {
                return null;
            }

            return queuedAnswer;
        }
    }
}
