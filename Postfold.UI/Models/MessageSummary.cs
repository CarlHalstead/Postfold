using Avalonia.Media;
using Postfold.Core.Enums;
using Postfold.Core.Models.Messages;
using Postfold.UI.Resources;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Postfold.UI.Models;

/// <summary>Local presentation of a cached summary; creating a row never retrieves message content.</summary>
public sealed record MessageSummary(MessageSnapshot Message, string Group, string AccountName, string Date, bool Unified = false)
{
    public string Sender => SenderPresentation.Create(Message.Participants).Name;
    public string Subject => string.IsNullOrWhiteSpace(Message.Subject) ? Strings.Get("NoSubject") : Message.Subject;
    public string Preview => Regex.Replace(Message.CachedPreview ?? "", @"\s+", " ").Trim();
    public bool HasPreview => !string.IsNullOrWhiteSpace(Preview);
    public string Initials => SenderPresentation.Create(Message.Participants).Initials;
    public bool HasGroup => Group.Length > 0;
    public bool Unread => !Message.Read;
    public bool HasAttachments => Message.HasKnownAttachments;
    public FontWeight Weight => Message.Read ? FontWeight.Normal : FontWeight.SemiBold;
    public string State => string.Join(" · ", new[]
    {
        Unified ? AccountName : "",
        Message.Flagged ? Strings.Flagged : "",
        Message.Pending is PendingState.None or PendingState.Completed or PendingState.Dismissed ? "" : Strings.Describe(Message.Pending)
    }.Where(value => value.Length > 0));
    public bool HasState => State.Length > 0;
    public string AccessibleName => string.Join(", ", new[]
    {
        Group, Message.Participants, Subject, Preview, AccountName,
        Strings.Get(Message.Read ? "ReadState" : "UnreadState"), State,
        HasAttachments ? Strings.Attachments : "", Message.Date.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)
    }.Where(value => value.Length > 0));
}
