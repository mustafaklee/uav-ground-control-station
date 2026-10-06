using System.Text;

namespace Gcs.Messaging;

/// <summary>
/// Builds topic routing keys from event names: <c>VehicleRegistered</c> → <c>vehicle.registered</c>.
/// The first word is the area, so a consumer can bind <c>vehicle.*</c> to receive every vehicle event.
/// </summary>
public static class EventRoutingKey
{
    public static string From(string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        var builder = new StringBuilder(eventType.Length + 4);
        for (var i = 0; i < eventType.Length; i++)
        {
            var c = eventType[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('.');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
