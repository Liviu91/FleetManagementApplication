namespace MauiApp1
{
    /// <summary>
    /// Update these two values before building the APK for the presentation:
    ///   1. Start ngrok:        ngrok http 7292
    ///   2. Copy the https URL  e.g. https://abc123.ngrok-free.app
    ///   3. Paste it in ApiBaseUrl below (keep trailing slash)
    ///   4. Paste your CloudAMQP URI in RabbitMqUri (from cloudamqp.com dashboard)
    ///   5. Rebuild and redeploy the APK
    /// </summary>
    public static class AppConfig
    {
        // ngrok public URL ? tunnels to laptop:7292
        public const string ApiBaseUrl = "https://issue-puzzle-banked.ngrok-free.dev/";

        // CloudAMQP connection URI  e.g. amqps://user:pass@fish.rmq.cloudamqp.com/vhost
        public const string RabbitMqUri = "amqps://urcobeue:zqakedWFbst8L7Fhb3wirUHsz4MDt7Ai@seal.lmq.cloudamqp.com/urcobeue";
    }
}
