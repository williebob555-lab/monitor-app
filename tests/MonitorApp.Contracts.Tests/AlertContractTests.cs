using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Contracts.Tests;

public sealed class AlertContractTests
{
    private static readonly Timestamp Now = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));

    private static AlertRecord Record() => new()
    {
        SchemaVersion = 1,
        AlertId = "a1",
        Revision = 1,
        RuleId = "peer.silent",
        SubjectId = "peer-x",
        OriginPeerId = "peer-origin",
        Severity = AlertSeverity.Warning,
        State = AlertState.Raised,
        Title = "Peer not seen",
        Detail = "detail",
        RaisedAt = Now,
    };

    [Fact]
    [Trait("Category", "Contract")]
    public void AlertMessagesAreAdditiveOneofMembersWithReservedNumbersAndRoundTrip()
    {
        var request = new PeerSessionRequest { AlertPull = new AlertPull { RequestId = "r", MaximumCount = 10 } };
        var response = new PeerSessionResponse { AlertBatch = new AlertBatch { RequestId = "r" } };
        response.AlertBatch.Alerts.Add(Record());

        Assert.Equal(8, PeerSessionRequest.Descriptor.FindFieldByName("alert_pull").FieldNumber);
        Assert.Equal(9, PeerSessionRequest.Descriptor.FindFieldByName("alert_ack").FieldNumber);
        Assert.Equal(11, PeerSessionResponse.Descriptor.FindFieldByName("alert_batch").FieldNumber);
        Assert.Equal(request, PeerSessionRequest.Parser.ParseFrom(request.ToByteArray()));
        Assert.Equal(response, PeerSessionResponse.Parser.ParseFrom(response.ToByteArray()));
        // Every pre-existing session message keeps its original field number.
        Assert.Equal(7, PeerSessionRequest.Descriptor.FindFieldByName("heartbeat").FieldNumber);
        Assert.Equal(10, PeerSessionResponse.Descriptor.FindFieldByName("proof").FieldNumber);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void AnOlderParserPreservesUnknownAlertFieldsAndNewerFieldsDoNotBreakValidation()
    {
        // A future revision adds field 13 (reserved range) and an enum value this build does not know.
        var future = Record();
        var bytes = future.ToByteArray().Concat(new byte[] { 0x68, 0x2A }).ToArray(); // field 13, varint 42
        var parsed = AlertRecord.Parser.ParseFrom(bytes);
        Assert.Equal(future.AlertId, parsed.AlertId);
        Assert.Equal(bytes.Length, parsed.CalculateSize());

        var batch = new AlertBatch { RequestId = "r" };
        batch.Alerts.Add(parsed);
        Assert.True(PeerContractValidator.Validate(batch).IsValid);

        var unknownEnum = Record();
        unknownEnum.Severity = (AlertSeverity)99;
        batch.Alerts.Add(unknownEnum);
        Assert.False(PeerContractValidator.Validate(batch).IsValid);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void ValidatorEnforcesPullBatchAndAckLimits()
    {
        Assert.True(PeerContractValidator.Validate(new AlertPull { RequestId = "r", MaximumCount = 100 }).IsValid);
        Assert.False(PeerContractValidator.Validate(new AlertPull { RequestId = "r", MaximumCount = 0 }).IsValid);
        Assert.False(PeerContractValidator.Validate(new AlertPull { RequestId = "r", MaximumCount = 101 }).IsValid);
        Assert.False(PeerContractValidator.Validate(new AlertPull { MaximumCount = 5 }).IsValid);

        // An unsolicited live push has no request id and is still valid.
        var push = new AlertBatch();
        push.Alerts.Add(Record());
        Assert.True(PeerContractValidator.Validate(push).IsValid);

        var duplicate = new AlertBatch();
        duplicate.Alerts.Add(Record());
        duplicate.Alerts.Add(Record());
        Assert.False(PeerContractValidator.Validate(duplicate).IsValid);

        var resolvedWithoutTime = Record();
        resolvedWithoutTime.State = AlertState.Resolved;
        resolvedWithoutTime.Revision = 2;
        var invalid = new AlertBatch();
        invalid.Alerts.Add(resolvedWithoutTime);
        Assert.False(PeerContractValidator.Validate(invalid).IsValid);
        resolvedWithoutTime.ResolvedAt = Now;
        Assert.True(PeerContractValidator.Validate(invalid).IsValid);

        var hasMoreEmpty = new AlertBatch { HasMore = true };
        Assert.False(PeerContractValidator.Validate(hasMoreEmpty).IsValid);

        var ack = new AlertAck { RequestId = "r" };
        Assert.False(PeerContractValidator.Validate(ack).IsValid);
        ack.Entries.Add(new AlertAckEntry { AlertId = "a1", Revision = 0 });
        Assert.False(PeerContractValidator.Validate(ack).IsValid);
        ack.Entries[0].Revision = 1;
        Assert.True(PeerContractValidator.Validate(ack).IsValid);

        var oversizedText = Record();
        oversizedText.Detail = new string('x', PeerContractRules.MaximumAlertTextBytes + 1);
        var oversized = new AlertBatch();
        oversized.Alerts.Add(oversizedText);
        Assert.False(PeerContractValidator.Validate(oversized).IsValid);
    }
}
