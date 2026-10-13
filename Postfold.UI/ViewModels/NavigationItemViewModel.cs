using CommunityToolkit.Mvvm.ComponentModel;
using Postfold.UI.Models;
using System.Collections.Immutable;

namespace Postfold.UI.ViewModels;

/// <summary>Native tree navigation; account expansion stays independent of the selected mail scope.</summary>
public sealed class NavigationItemViewModel : ObservableObject
{
    private bool _isExpanded = true;

    public NavigationItemViewModel(MailScope scope, ImmutableArray<NavigationItemViewModel> children)
    {
        Scope = scope;
        Children = children;
    }

    public MailScope Scope
    {
        get;
    }
    public string Name => Scope.Name.TrimStart();
    public bool IsFolder => Scope.IsFolder;
    public ImmutableArray<NavigationItemViewModel> Children
    {
        get;
    }
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}
