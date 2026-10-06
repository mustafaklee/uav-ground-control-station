using Gcs.Messaging;

namespace Gcs.UnitTests.Messaging;

public sealed class EventRoutingKeyTests
{
    [Theory]
    [InlineData("VehicleRegistered", "vehicle.registered")]
    [InlineData("VehicleRetired", "vehicle.retired")]
    [InlineData("MissionUploadCompleted", "mission.upload.completed")]
    public void Event_name_becomes_a_dotted_lower_case_routing_key(string eventType, string expected)
    {
        EventRoutingKey.From(eventType).ShouldBe(expected);
    }
}
