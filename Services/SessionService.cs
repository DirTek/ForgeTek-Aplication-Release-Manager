using CommunityToolkit.Mvvm.ComponentModel;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

/// <summary>
/// Holds the signed-in user and derives permissions from their role. When no users exist the app
/// is "unprotected" and every permission is granted (preserving the original single-user behaviour).
/// </summary>
public partial class SessionService : ObservableObject, ISessionService
{
    private readonly IUserService _users;

    [ObservableProperty] private AppUser? _current;

    public SessionService(IUserService users) => _users = users;

    public bool IsProtected => _users.HasAnyUsers && Current is not null;

    public string ActorName => Current?.Username ?? Environment.UserName;

    public void SignIn(AppUser user) => Current = user;
    public void SignOut() => Current = null;

    // QA Testers can scan to evaluate a build they're reviewing, but cannot publish.
    public bool CanScan          => Allowed(r => r is UserRole.Admin or UserRole.Publisher or UserRole.Scanner or UserRole.QaTester);
    public bool CanPublish       => Allowed(r => r is UserRole.Admin or UserRole.Publisher);
    public bool CanManageSetups  => Allowed(r => r is UserRole.Admin or UserRole.SetupBuilder);
    public bool CanManageUsers   => Allowed(r => r is UserRole.Admin);

    // Binding approve/reject vote — Admin or QA Tester. Review notes — anyone signed in.
    public bool CanApprove       => Allowed(r => r is UserRole.Admin or UserRole.QaTester);
    public bool CanReviewNote    => Allowed(_ => true);

    // Unprotected (no users) → everything allowed; otherwise the current user's role decides.
    private bool Allowed(Func<UserRole, bool> predicate)
        => !_users.HasAnyUsers || (Current is not null && predicate(Current.Role));

    partial void OnCurrentChanged(AppUser? value)
    {
        OnPropertyChanged(nameof(IsProtected));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(CanManageSetups));
        OnPropertyChanged(nameof(CanManageUsers));
        OnPropertyChanged(nameof(CanApprove));
        OnPropertyChanged(nameof(CanReviewNote));
    }
}
