using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Postfold.Core.Enums;
using Postfold.UI.Models;
using Postfold.UI.Resources;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;

namespace Postfold.UI.ViewModels;

/// <summary>Native tree navigation; account expansion stays independent of the selected mail scope.</summary>
public sealed class NavigationItemViewModel : ObservableObject
{
    private readonly MailboxRole _role;
    private readonly string? _displayName;
    private readonly int _depth;
    private bool _isExpanded = true;
    private long? _unreadCount;

    public NavigationItemViewModel(
        MailScope scope,
        ImmutableArray<NavigationItemViewModel> children,
        MailboxRole role = MailboxRole.Unresolved,
        string? displayName = null,
        int depth = 0
    )
    {
        Scope = scope;
        Children = children;
        _role = role;
        _displayName = displayName;
        _depth = depth;
    }

    public MailScope Scope { get; }
    public string Name => _displayName ?? Scope.Name.TrimStart();
    public bool IsFolder => Scope.IsFolder;
    public bool IsAccount => Scope.IsAccount;
    public Avalonia.Thickness RowMargin => new Avalonia.Thickness(IsFolder ? 32 + _depth * 16 : 12, 8, 12, 8);
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

    /// <summary>Includes nested labels for selection, counts and retained expansion state.</summary>
    public IEnumerable<NavigationItemViewModel> DescendantsAndSelf()
    {
        yield return this;
        foreach (NavigationItemViewModel child in Children)
        {
            foreach (NavigationItemViewModel item in child.DescendantsAndSelf())
            {
                yield return item;
            }
        }
    }
}
