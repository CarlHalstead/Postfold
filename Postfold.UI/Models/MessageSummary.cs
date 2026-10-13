using Avalonia.Media;
using Postfold.Core.Models.Messages;
using Postfold.UI.Resources;
using System;
using System.Globalization;
using System.Linq;

namespace Postfold.UI.Models;

/// <summary>Local presentation of a cached summary; creating a row never retrieves message content.</summary>
public sealed record MessageSummary(MessageSnapshot Message, string Group, string AccountName, string Date)
{
    public string Sender => Message.Participants;
    public string Subject => Message.Subject;
    public string Preview => Message.CachedPreview ?? "";
    public bool HasPreview => !string.IsNullOrWhiteSpace(Preview);
    public string Initials => string.Concat(Sender.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2)
        .Select(word => char.ToUpper(word[0], CultureInfo.CurrentCulture)));
    public bool HasGroup => Group.Length > 0;
    public bool Unread => !Message.Read;
    public bool HasAttachments => Message.HasKnownAttachments;
    public FontWeight Weight => Message.Read ? FontWeight.Normal : FontWeight.SemiBold;
    public string State => Strings.Get(Message.Read ? "ReadState" : "UnreadState") +
        (Message.Flagged ? " · " + Strings.Get("Flagged") : "") +
        (Message.Pending == Postfold.Core.Enums.PendingState.None ? "" : " · " + Strings.Describe(Message.Pending));
}
