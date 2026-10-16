using Postfold.Desktop.Models.Updates;
using System;

namespace Postfold.Desktop.Interfaces;

/// <summary>Local connectivity/cost hints; Unknown is online with unproven cost, never unmetered.</summary>
internal interface IUpdateConnection
{
    UpdateConnection Current { get; }

    event EventHandler? Changed;
}
