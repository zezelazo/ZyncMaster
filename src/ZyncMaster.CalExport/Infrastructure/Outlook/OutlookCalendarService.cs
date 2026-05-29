using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Office.Interop.Outlook;
using ZyncMaster.Core;

namespace ZyncMaster.CalExport;

public sealed class OutlookCalendarService : ICalendarService
{
    private static class MeetingStatusCode
    {
        internal const int CanceledByOrganizer = 5;
        internal const int ReceivedAndCanceled = 7;
    }

    private static class ResponseStatusCode
    {
        internal const int Organizer    = 1;
        internal const int Tentative    = 2;
        internal const int Accepted     = 3;
        internal const int Declined     = 4;
        internal const int NotResponded = 5;
    }

    private static class RecipientTypeCode
    {
        internal const int Required = 1;
        internal const int Optional = 2;
        internal const int Resource = 3;
    }

    // Namespace for UUID v5 fallback IDs when GlobalAppointmentID is unavailable.
    // Generated once; treat as a constant — never change, or existing IDs drift.
    private static readonly Guid AppointmentIdNamespace =
        new Guid("6f0e7f2c-9c4a-4f7e-b6f5-ac6f0a3e3c11");

    // -----------------------------------------------------------------------
    // ICalendarService — GetCalendarFolders
    // -----------------------------------------------------------------------

    public IReadOnlyList<CalendarFolderInfo> GetCalendarFolders()
    {
        Application? app   = null;
        bool         owned = false;
        var          result = new List<CalendarFolderInfo>();

        try
        {
            bool wasRunning = System.Diagnostics.Process.GetProcessesByName("OUTLOOK").Length > 0;
            try
            {
                app   = new Application();
                owned = !wasRunning;
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    "Could not connect to Outlook. Make sure Outlook Classic is installed and configured on this machine.", ex);
            }

            NameSpace? ns = null;
            try
            {
                ns = app.GetNamespace("MAPI");
                foreach (Store store in ns.Stores)
                {
                    MAPIFolder? root = null;
                    try
                    {
                        var storeName = store.DisplayName ?? store.StoreID ?? "";
                        root = store.GetRootFolder();
                        CollectCalendarFolders(root, storeName, result);
                    }
                    catch (COMException) { /* skip inaccessible or non-calendar stores */ }
                    finally
                    {
                        if (root  != null) try { Marshal.ReleaseComObject(root);  } catch { }
                        try { Marshal.ReleaseComObject(store); } catch { }
                    }
                }
            }
            finally
            {
                if (ns != null) try { Marshal.ReleaseComObject(ns); } catch { }
            }
        }
        finally
        {
            if (owned && app != null)
            {
                try { app.Quit(); } catch { }
                Marshal.ReleaseComObject(app);
            }
        }

        return result;
    }

    private static void CollectCalendarFolders(MAPIFolder folder, string storeName, List<CalendarFolderInfo> result)
    {
        if (folder.DefaultItemType == OlItemType.olAppointmentItem)
        {
            result.Add(new CalendarFolderInfo
            {
                DisplayName = $"{folder.Name} [{storeName}]",
                EntryId     = folder.EntryID,
                StoreId     = folder.StoreID,
            });
        }

        foreach (MAPIFolder sub in folder.Folders)
        {
            try { CollectCalendarFolders(sub, storeName, result); }
            catch (COMException) { }
            finally { try { Marshal.ReleaseComObject(sub); } catch { } }
        }
    }

    // -----------------------------------------------------------------------
    // ICalendarService — GetAppointments
    // -----------------------------------------------------------------------

    public IReadOnlyList<AppointmentRecord> GetAppointments(ExportParameters parameters)
    {
        if (parameters == null) throw new ArgumentNullException(nameof(parameters));

        Application? app   = null;
        bool         owned = false;

        try
        {
            bool wasRunning = System.Diagnostics.Process.GetProcessesByName("OUTLOOK").Length > 0;
            try
            {
                app   = new Application();
                owned = !wasRunning;
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    "Could not connect to Outlook. Make sure Outlook Classic is installed and configured on this machine.", ex);
            }

            var results = new List<AppointmentRecord>();
            var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var rangeStart = new DateTime(parameters.Year, parameters.Month, 1, 0, 0, 0);
            var rangeEnd   = rangeStart.AddMonths(1);
            var filterStr  = $"[Start] >= '{rangeStart:MM/dd/yyyy HH:mm}' AND [Start] < '{rangeEnd:MM/dd/yyyy HH:mm}'";

            NameSpace? ns = null;
            try
            {
                ns = app.GetNamespace("MAPI");

                if (parameters.SelectedFolders == null)
                {
                    foreach (Store store in ns.Stores)
                    {
                        MAPIFolder? root = null;
                        try
                        {
                            root = store.GetRootFolder();
                            CollectFromFolder(root, filterStr, rangeStart, rangeEnd, parameters.Mode, parameters.IncludeCancelled, seen, results);
                        }
                        catch (COMException) { }
                        finally
                        {
                            if (root != null) try { Marshal.ReleaseComObject(root); } catch { }
                            try { Marshal.ReleaseComObject(store); } catch { }
                        }
                    }
                }
                else
                {
                    foreach (var fi in parameters.SelectedFolders)
                    {
                        MAPIFolder? folder = null;
                        try
                        {
                            folder = ns.GetFolderFromID(fi.EntryId, fi.StoreId);
                            CollectFromFolder(folder, filterStr, rangeStart, rangeEnd, parameters.Mode, parameters.IncludeCancelled, seen, results);
                        }
                        catch (COMException) { }
                        finally
                        {
                            if (folder != null) try { Marshal.ReleaseComObject(folder); } catch { }
                        }
                    }
                }
            }
            finally
            {
                if (ns != null) try { Marshal.ReleaseComObject(ns); } catch { }
            }

            return results;
        }
        finally
        {
            if (owned && app != null)
            {
                try { app.Quit(); } catch { }
                Marshal.ReleaseComObject(app);
            }
        }
    }

    private static void CollectFromFolder(
        MAPIFolder          folder,
        string              filter,
        DateTime            rangeStart,
        DateTime            rangeEnd,
        ExportMode          mode,
        bool                includeCancelled,
        HashSet<string>     seen,
        List<AppointmentRecord> results)
    {
        if (folder.DefaultItemType == OlItemType.olAppointmentItem)
        {
            var items = folder.Items;
            // Restrict returns a NEW COM Items collection — even though it looks "derived"
            // from `items`, it's a separate object with its own RCW that must be released
            // explicitly. Letting it leak accumulates COM handles when iterating many
            // folders ("all calendars" exports) and delays Outlook resource cleanup
            // until non-deterministic GC.
            Items? filtered = null;
            try
            {
                // IncludeRecurrences + Sort("[Start]") MUST be set before Restrict
                // to expand recurring series into individual occurrences.
                items.IncludeRecurrences = true;
                items.Sort("[Start]");

                filtered = items.Restrict(filter);
                foreach (object raw in filtered)
                {
                    if (raw is AppointmentItem appt)
                    {
                        try
                        {
                            // Read GlobalAppointmentID FIRST — before touching Start or any other
                            // property. On recurring occurrences, accessing it after Start can
                            // corrupt the COM context and make subsequent reads return the series
                            // master date instead of the occurrence date.
                            var globalId = TryGetGlobalAppointmentId(appt);

                            // Capture Start immediately for the same reason.
                            var start = appt.Start;

                            if (start < rangeStart || start >= rangeEnd)
                                continue;

                            var meetingStatus = (int)appt.MeetingStatus;
                            var subject       = appt.Subject ?? "";
                            bool isCancelled  = meetingStatus == MeetingStatusCode.CanceledByOrganizer ||
                                                meetingStatus == MeetingStatusCode.ReceivedAndCanceled ||
                                                IsCancelledSubject(subject);

                            if (isCancelled && !includeCancelled)
                                continue;

                            var record = BuildRecord(appt, start, subject, mode, isCancelled, globalId);

                            var key = $"{start:yyyy-MM-dd HH:mm}|{subject}";
                            if (seen.Add(key))
                                results.Add(record);
                        }
                        catch (System.Exception) { /* skip malformed individual appointment items */ }
                        finally { try { Marshal.ReleaseComObject(raw); } catch { } }
                    }
                }
            }
            finally
            {
                if (filtered != null) try { Marshal.ReleaseComObject(filtered); } catch { }
                try { Marshal.ReleaseComObject(items); } catch { }
            }
        }

        foreach (MAPIFolder sub in folder.Folders)
        {
            try { CollectFromFolder(sub, filter, rangeStart, rangeEnd, mode, includeCancelled, seen, results); }
            catch (COMException) { }
            finally { try { Marshal.ReleaseComObject(sub); } catch { } }
        }
    }

    private static bool IsCancelledSubject(string subject) =>
        subject.StartsWith("Canceled:",  StringComparison.OrdinalIgnoreCase) ||
        subject.StartsWith("Cancelled:", StringComparison.OrdinalIgnoreCase);

    private static AppointmentRecord BuildRecord(
        AppointmentItem appt,
        DateTime        start,
        string          subject,
        ExportMode      mode,
        bool            isCancelled,
        string          globalAppointmentId)
    {
        var titleForRecord = subject.Length > 0 ? subject : "(no title)";

        if (mode == ExportMode.Complete)
        {
            var description = TryGetBody(appt);
            var (startOffset, endOffset, tzId, tzDisplayName) = GetTimezoneInfo(appt, start);
            var participants   = GetParticipants(appt);
            var organizerEmail = TryGetOrganizerEmail(appt);
            var id             = ResolveId(globalAppointmentId, organizerEmail, startOffset, titleForRecord);

            return new AppointmentRecord
            {
                Id                       = id,
                Start                    = start,
                Duration                 = appt.Duration,
                IsAllDay                 = appt.AllDayEvent,
                Subject                  = titleForRecord,
                OrganizerName            = appt.Organizer ?? "",
                OrganizerEmail           = organizerEmail,
                IsCancelled              = isCancelled,
                Description              = description,
                StartOffset              = startOffset,
                EndOffset                = endOffset,
                StartTimeZoneId          = tzId,
                StartTimeZoneDisplayName = tzDisplayName,
                Participants             = participants,
            };
        }
        else
        {
            var organizerEmail = TryGetOrganizerEmail(appt);
            var simpleOffset   = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start));
            var id             = ResolveId(globalAppointmentId, organizerEmail, simpleOffset, titleForRecord);

            return new AppointmentRecord
            {
                Id             = id,
                Start          = start,
                Duration       = appt.Duration,
                IsAllDay       = appt.AllDayEvent,
                Subject        = titleForRecord,
                OrganizerName  = appt.Organizer ?? "",
                OrganizerEmail = organizerEmail,
                IsCancelled    = isCancelled,
            };
        }
    }

    private static string TryGetGlobalAppointmentId(AppointmentItem appt)
    {
        try   { return appt.GlobalAppointmentID ?? ""; }
        catch { return ""; }
    }

    private static string ResolveId(string globalAppointmentId, string organizerEmail, DateTimeOffset start, string subject)
    {
        if (!string.IsNullOrEmpty(globalAppointmentId))
            return globalAppointmentId;

        var seed = string.Concat(
            organizerEmail, "|",
            start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), "|",
            subject);
        return UuidV5.Create(AppointmentIdNamespace, seed).ToString("D");
    }

    private static string TryGetBody(AppointmentItem appt)
    {
        try { return appt.Body ?? ""; }
        catch { return ""; }
    }

    private static (DateTimeOffset startOffset, DateTimeOffset endOffset, string tzId, string tzDisplayName)
        GetTimezoneInfo(AppointmentItem appt, DateTime start)
    {
        string   tzId          = "";
        string   tzDisplayName = "";
        dynamic? outlookTz     = null;

        try
        {
            outlookTz     = appt.StartTimeZone;
            tzId          = (string)(outlookTz.ID   ?? "");
            tzDisplayName = (string)(outlookTz.Name ?? "");
        }
        catch (System.Exception) { /* skip unreadable StartTimeZone COM properties */ }
        finally
        {
            if (outlookTz != null) try { Marshal.ReleaseComObject(outlookTz); } catch { }
        }

        TimeZoneInfo tzInfo;
        try
        {
            tzInfo = string.IsNullOrEmpty(tzId)
                ? TimeZoneInfo.Local
                : TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch (TimeZoneNotFoundException)
        {
            tzInfo = TimeZoneInfo.Local;
        }

        if (string.IsNullOrEmpty(tzId))
        {
            tzId          = tzInfo.Id;
            tzDisplayName = tzInfo.DisplayName;
        }

        var startOffset = new DateTimeOffset(start, tzInfo.GetUtcOffset(start));

        DateTime endLocal;
        try { endLocal = appt.End; }
        catch (System.Exception) { endLocal = start.AddMinutes(appt.Duration); }

        var endOffset = new DateTimeOffset(endLocal, tzInfo.GetUtcOffset(endLocal));

        return (startOffset, endOffset, tzId, tzDisplayName);
    }

    private static List<ParticipantRecord> GetParticipants(AppointmentItem appt)
    {
        var         result     = new List<ParticipantRecord>();
        Recipients? recipients = null;
        try
        {
            recipients = appt.Recipients;
            foreach (Recipient r in recipients)
            {
                try
                {
                    result.Add(new ParticipantRecord
                    {
                        Name     = r.Name ?? "",
                        Email    = GetRecipientEmail(r),
                        Type     = GetRecipientTypeLabel(r),
                        Response = GetParticipantResponseLabel(r),
                    });
                }
                catch (System.Exception) { /* skip malformed recipient */ }
                finally { try { Marshal.ReleaseComObject(r); } catch { } }
            }
        }
        catch (COMException) { }
        finally
        {
            if (recipients != null) try { Marshal.ReleaseComObject(recipients); } catch { }
        }
        return result;
    }

    private static string GetRecipientEmail(Recipient r)
    {
        try
        {
            var addr = r.AddressEntry;
            if (addr == null) return "";
            try
            {
                if (addr.AddressEntryUserType is
                    OlAddressEntryUserType.olExchangeUserAddressEntry or
                    OlAddressEntryUserType.olExchangeRemoteUserAddressEntry)
                {
                    return addr.GetExchangeUser()?.PrimarySmtpAddress ?? addr.Address ?? "";
                }
                return addr.Address ?? "";
            }
            finally { try { Marshal.ReleaseComObject(addr); } catch { } }
        }
        catch { return ""; }
    }

    private static string GetRecipientTypeLabel(Recipient r)
    {
        try
        {
            return (int)r.Type switch
            {
                RecipientTypeCode.Required => "required",
                RecipientTypeCode.Optional => "optional",
                RecipientTypeCode.Resource => "resource",
                _                          => "unknown",
            };
        }
        catch { return "unknown"; }
    }

    private static string GetParticipantResponseLabel(Recipient r)
    {
        try
        {
            return (int)r.MeetingResponseStatus switch
            {
                ResponseStatusCode.Organizer    => "organizer",
                ResponseStatusCode.Tentative    => "tentative",
                ResponseStatusCode.Accepted     => "accepted",
                ResponseStatusCode.Declined     => "declined",
                ResponseStatusCode.NotResponded => "notResponded",
                _                               => "none",
            };
        }
        catch { return "none"; }
    }

    private static string TryGetOrganizerEmail(AppointmentItem appt)
    {
        if (string.IsNullOrEmpty(appt.Organizer))
            return "";

        Recipients? recipients = null;
        try
        {
            recipients = appt.Recipients;
            foreach (Recipient r in recipients)
            {
                try
                {
                    if (!string.Equals(r.Name, appt.Organizer, StringComparison.OrdinalIgnoreCase))
                        continue;
                    return GetRecipientEmail(r);
                }
                catch { }
                finally { try { Marshal.ReleaseComObject(r); } catch { } }
            }
        }
        catch (COMException) { }
        finally
        {
            if (recipients != null) try { Marshal.ReleaseComObject(recipients); } catch { }
        }
        return "";
    }
}
