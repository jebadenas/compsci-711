namespace TotalOrder.Core;

public record MulticastResult(string MsgId, string DisplayText, string WireMessage);

public record MulticastReceivedResult(
    string MsgId,
    string Text,
    int ProposedValue,
    int ProposerId,
    string ReceivedDisplayText,
    string ProposeWireMessage,
    int OriginPort);

public record FinalMessage(string WireMessage, int Port);

public record ProposeResult(string MsgId, int AgreedValue, int AgreedSenderId, List<FinalMessage> FinalMessages);

public record DeliveredMessage(string MsgId, string Text, int FinalTsValue, int FinalTsSenderId, string ReadyDisplayText);

public record FinalResult(List<DeliveredMessage> DeliveredMessages);

public class TotalOrderProtocol
{
    private readonly int _middlewareId;
    private readonly int _totalMiddleware;
    private readonly int _basePort;
    private int _msgCounter;
    private int _localClock;
    private readonly List<QueueEntry> _holdQueue = new();
    private readonly Dictionary<string, List<(int Value, int SenderId)>> _proposals = new();

    public TotalOrderProtocol(int middlewareId, int totalMiddleware, int basePort = 8081)
    {
        _middlewareId = middlewareId;
        _totalMiddleware = totalMiddleware;
        _basePort = basePort;
    }

    public MulticastResult CreateMulticast()
    {
        _msgCounter++;
        var msgId = $"{_middlewareId}:{_msgCounter}";
        var text = $"Msg #{_msgCounter} from Middleware {_middlewareId}";
        var wire = $"MULTICAST|{msgId}|{text}";
        return new MulticastResult(msgId, text, wire);
    }

    public MulticastReceivedResult OnMulticastReceived(string msgId, string text)
    {
        int maxInQueue = _holdQueue.Count > 0 ? _holdQueue.Max(e => e.TsValue) : 0;
        int proposed = Math.Max(_localClock, maxInQueue) + 1;
        _localClock = proposed;

        _holdQueue.Add(new QueueEntry
        {
            MsgId = msgId,
            Text = text,
            TsValue = proposed,
            TsSenderId = _middlewareId,
            IsFinal = false
        });

        // Register this middleware's own proposal
        int originId = int.Parse(msgId.Split(':')[0]);
        if (originId == _middlewareId)
        {
            if (!_proposals.ContainsKey(msgId))
                _proposals[msgId] = new();
            _proposals[msgId].Add((proposed, _middlewareId));
        }

        int originPort = _basePort + originId;

        var displayText = $"{text} | proposed:({proposed},{_middlewareId})";
        var wireMessage = $"PROPOSE|{msgId}|{proposed}|{_middlewareId}";

        return new MulticastReceivedResult(msgId, text, proposed, _middlewareId, displayText, wireMessage, originPort);
    }

    public ProposeResult? OnProposeReceived(string msgId, int value, int senderId)
    {
        if (!_proposals.ContainsKey(msgId))
            _proposals[msgId] = new();

        _proposals[msgId].Add((value, senderId));

        if (_proposals[msgId].Count < _totalMiddleware)
            return null;

        // All proposals collected — find the agreed timestamp
        int agreedVal = 0;
        int agreedSid = 0;
        foreach (var (v, s) in _proposals[msgId])
        {
            if (v > agreedVal || (v == agreedVal && s > agreedSid))
            {
                agreedVal = v;
                agreedSid = s;
            }
        }
        _proposals.Remove(msgId);

        // Build FINAL messages for all middleware
        var finalWire = $"FINAL|{msgId}|{agreedVal}|{agreedSid}";
        var finalMessages = new List<FinalMessage>();
        for (int i = 1; i <= _totalMiddleware; i++)
            finalMessages.Add(new FinalMessage(finalWire, _basePort + i));

        return new ProposeResult(msgId, agreedVal, agreedSid, finalMessages);
    }

    public FinalResult OnFinalReceived(string msgId, int value, int senderId)
    {
        _localClock = Math.Max(_localClock, value);

        var entry = _holdQueue.FirstOrDefault(e => e.MsgId == msgId);
        if (entry == null)
            return new FinalResult(new List<DeliveredMessage>());

        entry.TsValue = value;
        entry.TsSenderId = senderId;
        entry.IsFinal = true;

        // Re-sort by (TsValue, TsSenderId)
        _holdQueue.Sort((a, b) =>
            a.TsValue != b.TsValue
                ? a.TsValue.CompareTo(b.TsValue)
                : a.TsSenderId.CompareTo(b.TsSenderId));

        // Deliver consecutive final entries from the head
        var delivered = new List<DeliveredMessage>();
        while (_holdQueue.Count > 0 && _holdQueue[0].IsFinal)
        {
            var e = _holdQueue[0];
            _holdQueue.RemoveAt(0);
            delivered.Add(new DeliveredMessage(
                e.MsgId,
                e.Text,
                e.TsValue,
                e.TsSenderId,
                $"{e.Text} | final:({e.TsValue},{e.TsSenderId})"
            ));
        }

        return new FinalResult(delivered);
    }

    /// <summary>
    /// Parses a raw wire message and dispatches to the appropriate handler.
    /// Returns an action descriptor for the caller to execute I/O.
    /// </summary>
    public ProtocolAction ProcessMessage(string raw)
    {
        string[] parts = raw.Split('|');
        if (parts.Length < 2)
            return new NoOpAction();

        switch (parts[0])
        {
            case "MULTICAST" when parts.Length >= 3:
            {
                var result = OnMulticastReceived(parts[1], parts[2]);
                return new SendProposeAction(result);
            }
            case "PROPOSE" when parts.Length >= 4:
            {
                var result = OnProposeReceived(parts[1], int.Parse(parts[2]), int.Parse(parts[3]));
                if (result != null)
                    return new BroadcastFinalAction(result);
                return new NoOpAction();
            }
            case "FINAL" when parts.Length >= 4:
            {
                var result = OnFinalReceived(parts[1], int.Parse(parts[2]), int.Parse(parts[3]));
                return new DeliverAction(result);
            }
            default:
                return new NoOpAction();
        }
    }
}

public class QueueEntry
{
    public string MsgId = "";
    public string Text = "";
    public int TsValue;
    public int TsSenderId;
    public bool IsFinal;
}

// Action types returned by ProcessMessage for the caller to execute I/O
public abstract record ProtocolAction;
public record NoOpAction() : ProtocolAction;
public record SendProposeAction(MulticastReceivedResult Result) : ProtocolAction;
public record BroadcastFinalAction(ProposeResult Result) : ProtocolAction;
public record DeliverAction(FinalResult Result) : ProtocolAction;
