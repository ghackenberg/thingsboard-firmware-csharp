using MQTTnet;
using MQTTnet.Client;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

var file = Process.GetCurrentProcess().MainModule?.FileName;

Console.WriteLine($"Running {file}");

const string THINGSBOARD_HOST = "192.168.137.1";
const int THINGSBOARD_PORT = 1883;
const string THINGSBOARD_TOKEN = "bvdoxulzQYsYUshqfiSE";

var builder = new MqttClientOptionsBuilder();
var options = builder.WithTcpServer(THINGSBOARD_HOST, THINGSBOARD_PORT).WithCredentials(THINGSBOARD_TOKEN).Build();
var factory = new MqttFactory();
var client = factory.CreateMqttClient();

var firmwareRequestCount = -1;
var firmwareChunkSize = 1024 * 4;
var firmwareSize = 0;
var firmwareTitle = "";
var firmwareVersion = "";
var firmwareBuffer = new byte[firmwareSize];

var active = true;

var random = new Random();

client.ConnectedAsync += async e =>
{
    Console.WriteLine("Connected");

    // Response to initial attribute request
    await client.SubscribeAsync("v1/devices/me/attributes/response/+");
    // Attribute update
    await client.SubscribeAsync("v1/devices/me/attributes");
    // Firmware chunk
    await client.SubscribeAsync("v2/fw/response/+/chunk/+");

    // Send initial attribute request
    Console.WriteLine("Sending initial attribute request");

    await client.PublishStringAsync("v1/devices/me/attributes/request/0", "{}");
};
client.DisconnectedAsync += async e =>
{
    Console.WriteLine("Disconnected");

    if (active)
    {
        await client.ReconnectAsync();
    }
};
client.ApplicationMessageReceivedAsync += async e =>
{
    var topic = e.ApplicationMessage.Topic;

    // Response to initial attribute request
    if (topic.StartsWith("v1/devices/me/attributes/response/"))
    {
        Console.WriteLine("Receiving response to initial attribute request");

        var payload = e.ApplicationMessage.ConvertPayloadToString();

        var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var shared = root.GetProperty("shared");
        var checksum = shared.GetProperty("fw_checksum").GetString();
        var checksum_algorithm = shared.GetProperty("fw_checksum_algorithm").GetString();
        var size = shared.GetProperty("fw_size").GetInt32();
        var tag = shared.GetProperty("fw_tag").GetString();
        var title = shared.GetProperty("fw_title").GetString();
        var version = shared.GetProperty("fw_version").GetString();

        // Initialize buffer
        firmwareSize = size;
        firmwareTitle = title;
        firmwareVersion = version;
        firmwareBuffer = new byte[size];
        firmwareRequestCount++;

        // Request first chunk
        if (file != null && !file.EndsWith(".exe") && !file.EndsWith($"{firmwareTitle}-{firmwareVersion}"))
        {
            Console.WriteLine("Starting firmware download");

            await client.PublishStringAsync($"v2/fw/request/{firmwareRequestCount}/chunk/0", $"{firmwareChunkSize}");
        }
    }
    // Attribute update
    else if (topic.Equals("v1/devices/me/attributes"))
    {
        Console.WriteLine("Receiving attribute update");

        var payload = e.ApplicationMessage.ConvertPayloadToString();

        var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var checksum = root.GetProperty("fw_checksum").GetString();
        var checksum_algorithm = root.GetProperty("fw_checksum_algorithm").GetString();
        var size = root.GetProperty("fw_size").GetInt32();
        var tag = root.GetProperty("fw_tag").GetString();
        var title = root.GetProperty("fw_title").GetString();
        var version = root.GetProperty("fw_version").GetString();

        // Initialize buffer
        firmwareSize = size;
        firmwareTitle = title;
        firmwareVersion = version;
        firmwareBuffer = new byte[size];
        firmwareRequestCount++;

        // Request first chunk
        if (file != null && !file.EndsWith(".exe") && !file.EndsWith($"{firmwareTitle}-{firmwareVersion}"))
        {
            Console.WriteLine("Starting firmware download");

            await client.PublishStringAsync($"v2/fw/request/{firmwareRequestCount}/chunk/0", $"{firmwareChunkSize}");
        }
    }
    // Firmware chunk
    else if (topic.StartsWith("v2/fw/response/"))
    {
        var payload = e.ApplicationMessage.PayloadSegment;

        var match = Regex.Match(topic, @"v2/fw/response/([0-9]*)/chunk/([0-9]*)");

        uint myFirmwareRequestCount;
        uint myFirmwareChunkIndex;

        uint.TryParse(match.Groups[1].Value, out myFirmwareRequestCount);
        uint.TryParse(match.Groups[2].Value, out myFirmwareChunkIndex);

        if (myFirmwareRequestCount == firmwareRequestCount)
        {
            if (payload.Count > 0)
            {
                Console.Write($"\rBytes {myFirmwareChunkIndex * firmwareChunkSize + payload.Count} / {firmwareSize}");

                // Update firmware buffer
                for (var i = 0; i < payload.Count; i++)
                {
                    firmwareBuffer[myFirmwareChunkIndex * firmwareChunkSize + i] = payload[i];
                }

                // Request next chunk
                await client.PublishStringAsync($"v2/fw/request/{myFirmwareRequestCount}/chunk/{myFirmwareChunkIndex + 1}", $"{firmwareChunkSize}");
            }
            else
            {
                // Write new firmware
                Console.WriteLine("\nWriting firmware file");

                await File.WriteAllBytesAsync($"{firmwareTitle}-{firmwareVersion}", firmwareBuffer);

                Process.Start("/bin/bash", $"-c \"chmod a+x {firmwareTitle}-{firmwareVersion}\"").WaitForExit();

                // Terminate old firmware
                active = false;
            }
        }
    }
};

// Connect to MQTT broker
Console.WriteLine("Connecting to MQTT broker");

await client.ConnectAsync(options);

// Send random telemetry
while (active)
{
    await client.PublishStringAsync("v1/devices/me/telemetry", $"{{\"random\":{random.Next(0, 100)}}}");

    await Task.Delay(1000);
}

// Disconnect from MQTT broker
Console.WriteLine("Disconnecting from MQTT broker");

await client.DisconnectAsync();

// Start new firmware
Console.WriteLine("Starting new firmware");

Process.Start($"{firmwareTitle}-{firmwareVersion}");