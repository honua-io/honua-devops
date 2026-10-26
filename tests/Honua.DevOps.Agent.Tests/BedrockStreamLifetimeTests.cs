using System.Buffers.Binary;
using System.Text;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Honua.DevOps.Agent.Providers;
using Microsoft.Extensions.AI;

namespace Honua.DevOps.Agent.Tests;

public class BedrockStreamLifetimeTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("early-exit")]
    [InlineData("cancel")]
    public async Task Streaming_DisposesResponseBodyForEveryExit(string exit)
    {
        using TrackingStream body = new(Frame(), waitAtEnd: exit == "cancel");
        using StreamingRuntime runtime = new(new ConverseStreamResponse
        {
            Stream = new ConverseStreamOutput(body)
        });
        using BedrockChatClientAdapter adapter = new(runtime, "fake-model", ownsRuntime: false);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await using (IAsyncEnumerator<ChatResponseUpdate> updates = adapter.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "synthetic")], cancellationToken: cancellation.Token).GetAsyncEnumerator())
        {
            Assert.True(await updates.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("synthetic", updates.Current.Text);
            if (exit == "complete")
            {
                Assert.False(await updates.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else if (exit == "cancel")
            {
                Task<bool> pending = updates.MoveNextAsync().AsTask();
                await body.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await cancellation.CancelAsync();
                // The pinned AWS event-stream decoder wraps read cancellation. Assert the
                // exact wrapper and cancellation cause, rather than accepting any failure.
                BedrockRuntimeEventStreamException failure = await Assert.ThrowsAsync<BedrockRuntimeEventStreamException>(async () =>
                    await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
                Assert.True(cancellation.IsCancellationRequested);
            }
        }
        Assert.True(body.WasDisposed);
        Assert.Equal(1, runtime.Calls);
    }

    // One real AWS event-stream frame, parsed by the pinned SDK's decoder/unmarshaller.
    private static byte[] Frame()
    {
        using MemoryStream headers = new();
        foreach ((string name, string value) in new[]
        {
            (":message-type", "event"), (":event-type", "contentBlockDelta"), (":content-type", "application/json")
        })
        {
            byte[] key = Encoding.UTF8.GetBytes(name);
            byte[] text = Encoding.UTF8.GetBytes(value);
            headers.WriteByte((byte)key.Length);
            headers.Write(key);
            headers.WriteByte(7); // AWS event-stream string header.
            byte[] length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)text.Length);
            headers.Write(length);
            headers.Write(text);
        }
        byte[] payload = Encoding.UTF8.GetBytes("{\"contentBlockIndex\":0,\"delta\":{\"text\":\"synthetic\"}}");
        byte[] frame = new byte[16 + headers.Length + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, frame.Length);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), (int)headers.Length);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), Crc(frame.AsSpan(0, 8)));
        headers.ToArray().CopyTo(frame, 12);
        payload.CopyTo(frame, 12 + (int)headers.Length);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(frame.Length - 4), Crc(frame.AsSpan(0, frame.Length - 4)));
        return frame;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte item in bytes)
        {
            value ^= item;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value >> 1) ^ ((value & 1) == 1 ? 0xedb88320u : 0u);
            }
        }
        return ~value;
    }

    private sealed class TrackingStream(byte[] bytes, bool waitAtEnd) : MemoryStream(bytes)
    {
        internal bool WasDisposed { get; private set; }
        internal TaskCompletionSource<bool> ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (waitAtEnd && Position >= Length)
            {
                ReadBlocked.TrySetResult(true);
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                throw new TimeoutException("Synthetic stream read was not cancelled.");
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class StreamingRuntime(ConverseStreamResponse response) : AmazonBedrockRuntimeClient(
        new BasicAWSCredentials("fake-key", "fake-secret"),
        new AmazonBedrockRuntimeConfig { RegionEndpoint = Amazon.RegionEndpoint.USEast2 })
    {
        internal int Calls { get; private set; }
        public override Task<ConverseStreamResponse> ConverseStreamAsync(
            ConverseStreamRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(response);
        }
    }
}
