namespace ForgeTekUpdatePackager.Models;

/// <summary>Fixed roles that gate what a signed-in user may do.</summary>
public enum UserRole
{
    /// <summary>Full access, including managing users.</summary>
    Admin,

    /// <summary>Scan, package, and publish updates (no user management).</summary>
    Publisher,

    /// <summary>Scan only.</summary>
    Scanner,

    /// <summary>Create and generate setup bundles only.</summary>
    SetupBuilder,
}
