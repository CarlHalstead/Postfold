using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Postfold.Core.Enums;
using Postfold.UI.Models;
using Postfold.UI.Resources;
using System.Collections.Immutable;
using System.Globalization;

namespace Postfold.UI.ViewModels;

/// <summary>Native tree navigation; account expansion stays independent of the selected mail scope.</summary>
public sealed class NavigationItemViewModel : ObservableObject
{
    private readonly MailboxRole _role;
    private bool _isExpanded = true;
    private long? _unreadCount;

    public NavigationItemViewModel(
        MailScope scope,
        ImmutableArray<NavigationItemViewModel> children,
        MailboxRole role = MailboxRole.Unresolved
    )
    {
        Scope = scope;
        Children = children;
        _role = role;
    }

    public MailScope Scope { get; }
    public string Name => Scope.Name.TrimStart();
    public bool IsFolder => Scope.IsFolder;
    public bool IsAccount => Scope.IsAccount;
    public Avalonia.Thickness RowMargin => new Avalonia.Thickness(IsFolder ? 32 : 12, 8, 12, 8);
    /// <summary>Resolves dispatcher-owned geometry when the view reads it, after unbound metadata preparation.</summary>
    public Geometry Icon => Scope.IsAccount ? MailIcons.Account : MailIcons.ForRole(Scope.IsFolder ? _role : MailboxRole.Inbox);
    public string CountText => UnreadCount > 0 ? UnreadCount.Value.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string AccessibleName => Name + (UnreadCount is long count ? ", " + count.ToString(CultureInfo.CurrentCulture) +
        " " + Strings.Get("UnreadMessages") : "");
    public long? UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (SetProperty(ref _unreadCount, value))
            {
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }
    public ImmutableArray<NavigationItemViewModel> Children { get; }
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}
