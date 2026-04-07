using Xunit;
using TotalOrder.Core;

namespace TotalOrder.Tests;

public class TotalOrderProtocolTests
{
    [Fact]
    public void CreateMulticast_ProducesCorrectWireFormat()
    {
        var protocol = new TotalOrderProtocol(middlewareId: 1, totalMiddleware: 5);

        var result = protocol.CreateMulticast();

        Assert.Equal("1:1", result.MsgId);
        Assert.Equal("Msg #1 from Middleware 1", result.DisplayText);
        Assert.Equal("MULTICAST|1:1|Msg #1 from Middleware 1", result.WireMessage);
    }

    [Fact]
    public void CreateMulticast_IncrementsSequenceNumber()
    {
        var protocol = new TotalOrderProtocol(middlewareId: 3, totalMiddleware: 5);

        var first = protocol.CreateMulticast();
        var second = protocol.CreateMulticast();

        Assert.Equal("3:1", first.MsgId);
        Assert.Equal("3:2", second.MsgId);
        Assert.Equal("Msg #2 from Middleware 3", second.DisplayText);
    }

    [Fact]
    public void OnMulticastReceived_ReturnsPropose()
    {
        var protocol = new TotalOrderProtocol(middlewareId: 2, totalMiddleware: 5);

        var result = protocol.OnMulticastReceived("1:1", "Msg #1 from Middleware 1");

        Assert.Equal("1:1", result.MsgId);
        Assert.Equal("Msg #1 from Middleware 1", result.Text);
        Assert.True(result.ProposedValue > 0);
        Assert.Equal(2, result.ProposerId);
        // The display text shown in Received list
        Assert.Contains("proposed:", result.ReceivedDisplayText);
        // The wire message to send back to the originator
        Assert.Equal($"PROPOSE|1:1|{result.ProposedValue}|2", result.ProposeWireMessage);
        // The origin port to send PROPOSE to
        Assert.Equal(8082, result.OriginPort);  // middleware 1 listens on 8081 + 1
    }

    [Fact]
    public void OnMulticastReceived_ProposedTimestampsAreMonotonicallyIncreasing()
    {
        var protocol = new TotalOrderProtocol(middlewareId: 1, totalMiddleware: 5);

        var r1 = protocol.OnMulticastReceived("2:1", "Msg #1 from Middleware 2");
        var r2 = protocol.OnMulticastReceived("3:1", "Msg #1 from Middleware 3");
        var r3 = protocol.OnMulticastReceived("4:1", "Msg #1 from Middleware 4");

        Assert.True(r2.ProposedValue > r1.ProposedValue,
            $"Second proposal {r2.ProposedValue} should be > first {r1.ProposedValue}");
        Assert.True(r3.ProposedValue > r2.ProposedValue,
            $"Third proposal {r3.ProposedValue} should be > second {r2.ProposedValue}");
    }

    [Fact]
    public void OnProposeReceived_AfterAllProposals_ReturnsFinalWithMaxTimestamp()
    {
        // Middleware 1 sends a multicast, then collects 5 PROPOSEs
        var protocol = new TotalOrderProtocol(middlewareId: 1, totalMiddleware: 5);
        protocol.CreateMulticast(); // creates msgId "1:1"

        // Middleware 1 also receives the multicast back from the network
        protocol.OnMulticastReceived("1:1", "Msg #1 from Middleware 1");

        // Now collect 4 more PROPOSEs from other middleware (middleware 1 already proposed via OnMulticastReceived)
        var r2 = protocol.OnProposeReceived("1:1", 3, 2);
        Assert.Null(r2); // not all proposals in yet

        var r3 = protocol.OnProposeReceived("1:1", 5, 3);
        Assert.Null(r3);

        var r4 = protocol.OnProposeReceived("1:1", 2, 4);
        Assert.Null(r4);

        // The 5th proposal triggers agreement — we need middleware 1's own proposal too
        // Middleware 1 proposed via OnMulticastReceived, so that's proposal from sender 1
        // We still need sender 5's proposal to reach 5 total
        var r5 = protocol.OnProposeReceived("1:1", 4, 5);
        Assert.NotNull(r5);

        // Agreed timestamp should be max(proposals) = 5, from middleware 3
        Assert.Equal(5, r5!.AgreedValue);
        Assert.Equal(3, r5.AgreedSenderId);
        Assert.Equal("1:1", r5.MsgId);

        // Should produce FINAL wire messages for all 5 middleware
        Assert.Equal(5, r5.FinalMessages.Count);
        foreach (var fm in r5.FinalMessages)
        {
            Assert.Equal("FINAL|1:1|5|3", fm.WireMessage);
            Assert.True(fm.Port >= 8082 && fm.Port <= 8086);
        }
    }

    [Fact]
    public void OnProposeReceived_TiebreaksBySenderId()
    {
        var protocol = new TotalOrderProtocol(middlewareId: 1, totalMiddleware: 5);
        protocol.CreateMulticast();
        protocol.OnMulticastReceived("1:1", "Msg #1 from Middleware 1");

        protocol.OnProposeReceived("1:1", 3, 2);
        protocol.OnProposeReceived("1:1", 3, 3); // same value, higher sender ID
        protocol.OnProposeReceived("1:1", 3, 4);
        var result = protocol.OnProposeReceived("1:1", 3, 5);

        Assert.NotNull(result);
        // All proposed 3, tiebreak by highest sender ID = 5
        Assert.Equal(3, result!.AgreedValue);
        Assert.Equal(5, result.AgreedSenderId);
    }

    [Fact]
    public void OnFinalReceived_DeliversMessageWhenFinal()
    {
        var protocol = new TotalOrderProtocol(middlewareId: 2, totalMiddleware: 5);

        // Receive a multicast — it enters the hold queue as provisional
        protocol.OnMulticastReceived("1:1", "Msg #1 from Middleware 1");

        // Receive the FINAL — entry becomes final, head is final, so deliver
        var result = protocol.OnFinalReceived("1:1", 5, 3);

        Assert.Single(result.DeliveredMessages);
        Assert.Equal("Msg #1 from Middleware 1", result.DeliveredMessages[0].Text);
        Assert.Equal(5, result.DeliveredMessages[0].FinalTsValue);
        Assert.Equal(3, result.DeliveredMessages[0].FinalTsSenderId);
        Assert.Contains("final:(5,3)", result.DeliveredMessages[0].ReadyDisplayText);
    }

    [Fact]
    public void OnFinalReceived_DeliversMultipleConsecutiveFinalEntries()
    {
        var protocol = new TotalOrderProtocol(middlewareId: 2, totalMiddleware: 5);

        // Receive two multicasts
        protocol.OnMulticastReceived("1:1", "Msg A");
        protocol.OnMulticastReceived("3:1", "Msg B");

        // Finalize both — Msg A gets lower timestamp so it's first in queue
        protocol.OnFinalReceived("1:1", 2, 1);
        var result = protocol.OnFinalReceived("3:1", 3, 3);

        // Second FINAL should trigger delivery of Msg B (Msg A already delivered)
        Assert.Single(result.DeliveredMessages);
        Assert.Equal("Msg B", result.DeliveredMessages[0].Text);
    }

    [Fact]
    public void OnFinalReceived_SortsByTimestampThenSenderId()
    {
        var protocol = new TotalOrderProtocol(middlewareId: 2, totalMiddleware: 5);

        // Receive two multicasts — they get provisional timestamps 1 and 2
        protocol.OnMulticastReceived("1:1", "Msg A");
        protocol.OnMulticastReceived("3:1", "Msg B");

        // Finalize in reverse order with Msg B getting LOWER final timestamp
        // This should re-sort: Msg B (ts=1) before Msg A (ts=2)
        protocol.OnFinalReceived("3:1", 1, 3);  // Msg B gets timestamp 1
        var result = protocol.OnFinalReceived("1:1", 2, 1);  // Msg A gets timestamp 2

        // Both should be delivered, Msg B first due to lower timestamp
        Assert.Equal(2, result.DeliveredMessages.Count);
        Assert.Equal("Msg B", result.DeliveredMessages[0].Text);
        Assert.Equal("Msg A", result.DeliveredMessages[1].Text);
    }

    [Fact]
    public void OnFinalReceived_NonFinalHeadBlocksLaterFinals()
    {
        var protocol = new TotalOrderProtocol(middlewareId: 2, totalMiddleware: 5);

        // Receive three multicasts
        protocol.OnMulticastReceived("1:1", "Msg A");
        protocol.OnMulticastReceived("3:1", "Msg B");
        protocol.OnMulticastReceived("4:1", "Msg C");

        // Finalize Msg C (highest timestamp), but leave Msg A and Msg B provisional
        var result = protocol.OnFinalReceived("4:1", 10, 4);
        Assert.Empty(result.DeliveredMessages); // head is Msg A (provisional) — blocks everything

        // Finalize Msg B with timestamp 5 — still blocked by Msg A at head
        result = protocol.OnFinalReceived("3:1", 5, 3);
        Assert.Empty(result.DeliveredMessages);

        // Finalize Msg A with timestamp 1 — now all three are final, deliver all
        result = protocol.OnFinalReceived("1:1", 1, 1);
        Assert.Equal(3, result.DeliveredMessages.Count);
        Assert.Equal("Msg A", result.DeliveredMessages[0].Text);
        Assert.Equal("Msg B", result.DeliveredMessages[1].Text);
        Assert.Equal("Msg C", result.DeliveredMessages[2].Text);
    }

    [Fact]
    public void ConcurrentMessages_IndependentProposalRounds()
    {
        // Middleware 1 sends two messages, both in flight simultaneously
        var protocol = new TotalOrderProtocol(middlewareId: 1, totalMiddleware: 5);
        protocol.CreateMulticast(); // "1:1"
        protocol.CreateMulticast(); // "1:2"

        // Both come back via multicast
        protocol.OnMulticastReceived("1:1", "Msg A");
        protocol.OnMulticastReceived("1:2", "Msg B");

        // Collect proposals for "1:1"
        protocol.OnProposeReceived("1:1", 2, 2);
        protocol.OnProposeReceived("1:1", 3, 3);
        protocol.OnProposeReceived("1:1", 1, 4);
        var r1 = protocol.OnProposeReceived("1:1", 2, 5);
        Assert.NotNull(r1); // 5 proposals (including self) — should agree

        // Collect proposals for "1:2" independently
        protocol.OnProposeReceived("1:2", 4, 2);
        protocol.OnProposeReceived("1:2", 5, 3);
        protocol.OnProposeReceived("1:2", 3, 4);
        var r2 = protocol.OnProposeReceived("1:2", 4, 5);
        Assert.NotNull(r2);

        // They agreed on different timestamps
        Assert.NotEqual(r1!.AgreedValue, r2!.AgreedValue);
    }

    [Fact]
    public void OnFinalReceived_AdvancesLocalClock()
    {
        var protocol = new TotalOrderProtocol(middlewareId: 2, totalMiddleware: 5);

        // Receive a multicast — proposes timestamp 1
        protocol.OnMulticastReceived("1:1", "Msg A");

        // Receive FINAL with high timestamp
        protocol.OnFinalReceived("1:1", 100, 3);

        // Next proposal should be > 100 (clock advanced)
        var result = protocol.OnMulticastReceived("3:1", "Msg B");
        Assert.True(result.ProposedValue > 100,
            $"Proposed {result.ProposedValue} should be > 100 after FINAL with ts=100");
    }
}
