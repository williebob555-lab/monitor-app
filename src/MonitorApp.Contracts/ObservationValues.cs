using Google.Protobuf;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Contracts;

/// <summary>Reads the value out of a stored telemetry record: its text, true/false or number; null when it has none or is malformed.</summary>
public static class ObservationValues
{
    public static object? Read(byte[] recordBytes)
    {
        ArgumentNullException.ThrowIfNull(recordBytes);
        try
        {
            var value = TelemetryEnvelope.Parser.ParseFrom(recordBytes).Metric?.Value;
            return value?.ValueCase switch
            {
                MetricValue.ValueOneofCase.StringValue => value.StringValue,
                MetricValue.ValueOneofCase.BooleanValue => value.BooleanValue,
                MetricValue.ValueOneofCase.DoubleValue => value.DoubleValue,
                MetricValue.ValueOneofCase.UnsignedIntegerValue => value.UnsignedIntegerValue,
                _ => null,
            };
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
