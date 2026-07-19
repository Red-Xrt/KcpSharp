using System.Diagnostics.Metrics;

namespace KcpSharp.Tests;

public sealed class KcpMetricsListenerTests
{
    [Fact]
    public void RoundTripTime_Record_EmitsMeasurementCallback()
    {
        int samples = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == KcpMetrics.Meter.Name)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == KcpMetrics.RoundTripTime.Name)
            {
                samples++;
                Assert.True(measurement >= 0);
            }
        });
        listener.Start();

        KcpMetrics.RoundTripTime.Record(12.5);

        Assert.True(samples > 0, "Expected histogram Record to invoke measurement callback.");
    }

    [Fact]
    public async Task RoundTripTime_RecordedDuringLoopbackPing()
    {
        using var metrics = new StressMetricsCollector();
        await using var pair = LoopbackTestHelper.CreatePair(0xE001, LoopbackTestHelper.ServerJsonOptions(streamMode: false));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        for (int i = 0; i < 20; i++)
        {
            var payload = LoopbackTestHelper.CreateGameJson("ping", i, 128);
            await LoopbackTestHelper.RoundTripMessageAsync(pair.Local, pair.Remote, payload, cts.Token);
        }

        var snapshot = metrics.Finish();
        Assert.True(snapshot.KcpRttSampleCount > 0, "Expected KCP RTT histogram samples during loopback traffic.");
    }
}
