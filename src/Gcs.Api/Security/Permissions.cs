using Gcs.Contracts.Auth;

namespace Gcs.Api.Security;

/// <summary>
/// Authorization policies, named after what they allow, not after roles. Endpoints say "this needs Command",
/// and which roles have Command is decided in one table here. Changing who may do what never touches an endpoint.
/// <code>
///                     Observer  Operator  Maintenance  Administrator
/// Read                   ✓         ✓          ✓             ✓
/// LinkVehicles                     ✓          ✓             ✓
/// PlanMissions                     ✓                        ✓
/// Command                          ✓                        ✓
/// ManageVehicles                              ✓             ✓
/// ManageUsers                                               ✓
/// </code>
/// The matrix is tested endpoint by endpoint (AuthorizationMatrixTests).
/// </summary>
public static class Permissions
{
    /// <summary>See the fleet, telemetry, missions, leases and the command audit log.</summary>
    public const string Read = "read";

    /// <summary>Connect and disconnect vehicle links.</summary>
    public const string LinkVehicles = "vehicles.link";

    /// <summary>Create, edit, archive and upload missions; read the mission stored on a vehicle.</summary>
    public const string PlanMissions = "missions.plan";

    /// <summary>Take control of a vehicle and send it commands. Critical commands require this permission.</summary>
    public const string Command = "vehicles.command";

    /// <summary>Register, edit and retire vehicles.</summary>
    public const string ManageVehicles = "vehicles.manage";

    /// <summary>Create users, change roles, deactivate accounts.</summary>
    public const string ManageUsers = "users.manage";

    public static readonly IReadOnlyDictionary<string, string[]> RolesByPermission = new Dictionary<string, string[]>
    {
        [Read] = [Roles.Observer, Roles.Operator, Roles.Maintenance, Roles.Administrator],
        [LinkVehicles] = [Roles.Operator, Roles.Maintenance, Roles.Administrator],
        [PlanMissions] = [Roles.Operator, Roles.Administrator],
        [Command] = [Roles.Operator, Roles.Administrator],
        [ManageVehicles] = [Roles.Maintenance, Roles.Administrator],
        [ManageUsers] = [Roles.Administrator],
    };
}
