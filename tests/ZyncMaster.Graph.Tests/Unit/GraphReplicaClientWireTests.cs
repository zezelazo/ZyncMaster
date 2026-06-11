using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Graph.Tests;

// PRIVACY AT THE WIRE — spec §12. These tests capture the exact JSON GraphReplicaClient sends
// to Graph and assert the key set is the EXACT whitelist. The type system already prevents a
// ReplicaDraft from carrying private data (ReplicaDraftBuilderTests); this layer proves the
// serializer adds nothing on top.
public sealed class GraphReplicaClientWireTests
{
    private static readonly Guid ReplicaGuid = new("3d5a8f21-7b4e-4c96-9e1a-d2b6c0f47a83");
    private static readonly Guid CalImportGuid = new("6f0e7f2c-3b1a-4e8d-9c2f-7a5b1d9e4c30");

    private sealed class FakeTokens : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult("tok");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseJson;
        private readonly HttpStatusCode _status;
        public List<string> Bodies { get; } = new();
        public List<string> Urls { get; } = new();
        public List<HttpMethod> Methods { get; } = new();

        public CapturingHandler(string responseJson = "{\"id\":\"new-ev-1\"}",
            HttpStatusCode status = HttpStatusCode.OK)
        {
            _responseJson = responseJson;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method);
            Urls.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static GraphReplicaClient Client(CapturingHandler handler) =>
        new(new HttpClient(handler), new FakeTokens(), ReplicaGuid, CalImportGuid);

    private static ReplicaDraft Draft() => new()
    {
        MaskTitle = "Busy",
        Start = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero),
        TimeZoneId = "UTC",
        IsAllDay = false,
        ShowAs = "busy",
        SourceEventId = "7d3f9c2b-aaaa-bbbb-cccc-111122223333",
    };

    [Fact]
    public async Task Create_json_key_set_is_the_exact_whitelist()
    {
        var handler = new CapturingHandler();
        await Client(handler).CreateReplicaAsync("CAL", Draft());

        var body = JObject.Parse(handler.Bodies[0]);
        body.Properties().Select(p => p.Name).Should().BeEquivalentTo(new[]
        {
            "subject", "start", "end", "isAllDay", "showAs", "singleValueExtendedProperties",
        }, "every extra key in a replica payload is a privacy leak (spec §12); note there is " +
           "NO body, NO attendees, NO location, NO organizer, NO categories and NO reminder " +
           "(the destination calendar default applies)");
    }

    [Fact]
    public async Task Create_json_subject_is_the_mask_and_the_source_subject_appears_nowhere()
    {
        var handler = new CapturingHandler();
        await Client(handler).CreateReplicaAsync("CAL", Draft());

        var raw = handler.Bodies[0];
        JObject.Parse(raw)["subject"]!.Value<string>().Should().Be("Busy");
        raw.Should().NotContain("Secret", "the source subject must never travel");
        raw.Should().NotContain("@", "no email address may travel in a replica payload");
    }

    [Fact]
    public async Task Create_json_has_no_attendees_key_so_no_invitation_can_ever_be_sent()
    {
        var handler = new CapturingHandler();
        await Client(handler).CreateReplicaAsync("CAL", Draft());

        JObject.Parse(handler.Bodies[0]).ContainsKey("attendees").Should().BeFalse(
            "Graph attendees == invitation emails; a replica NEVER sends invitations");
    }

    [Fact]
    public async Task Create_json_extended_property_value_is_an_opaque_uuid_with_no_email_or_account_name()
    {
        var handler = new CapturingHandler();
        await Client(handler).CreateReplicaAsync("CAL", Draft());

        var props = (JArray)JObject.Parse(handler.Bodies[0])["singleValueExtendedProperties"]!;
        props.Should().HaveCount(1, "exactly the ZmReplicaOf mark, nothing else");
        props[0]!["id"]!.Value<string>().Should().Contain("ZmReplicaOf");
        props[0]!["id"]!.Value<string>().Should().Contain("3D5A8F21-7B4E-4C96-9E1A-D2B6C0F47A83",
            "the replica GUID must be its own, never CalImport's (engine separation)");
        props[0]!["value"]!.Value<string>().Should().Be("7d3f9c2b-aaaa-bbbb-cccc-111122223333");
        props[0]!["value"]!.Value<string>().Should().NotContain("@");
    }

    [Fact]
    public async Task Patch_times_json_never_carries_subject_or_body()
    {
        var handler = new CapturingHandler();
        await Client(handler).UpdateReplicaTimesAsync("ev-1", Draft());

        var body = JObject.Parse(handler.Bodies[0]);
        body.Properties().Select(p => p.Name).Should().BeEquivalentTo(new[]
        {
            "start", "end", "isAllDay", "showAs",
        }, "a propagation PATCH moves the replica with the origin but NEVER touches the manual " +
           "title (it belongs to the user) nor adds a body");
        handler.Methods[0].Method.Should().Be("PATCH");
    }

    [Fact]
    public async Task Update_subject_patches_only_the_subject()
    {
        var handler = new CapturingHandler();
        await Client(handler).UpdateSubjectAsync("ev-1", "Focus");

        var body = JObject.Parse(handler.Bodies[0]);
        body.Properties().Select(p => p.Name).Should().BeEquivalentTo(new[] { "subject" });
        body["subject"]!.Value<string>().Should().Be("Focus");
    }

    [Fact]
    public async Task Stamp_rule_processed_writes_only_the_rule_property()
    {
        var handler = new CapturingHandler();
        await Client(handler).StampRuleProcessedAsync("ev-1", "rule-1");

        var body = JObject.Parse(handler.Bodies[0]);
        body.Properties().Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "singleValueExtendedProperties" });
        var prop = ((JArray)body["singleValueExtendedProperties"]!)[0]!;
        prop["id"]!.Value<string>().Should().Contain("ZmRuleProcessed");
        prop["value"]!.Value<string>().Should().Be("rule-1");
    }

    [Fact]
    public async Task Get_event_returns_null_on_404_instead_of_throwing()
    {
        var handler = new CapturingHandler("{\"error\":{\"code\":\"ErrorItemNotFound\"}}",
            HttpStatusCode.NotFound);

        var snap = await Client(handler).GetEventAsync("gone-ev");

        snap.Should().BeNull("404 means 'origin deleted' — a legitimate propagation answer");
    }

    [Fact]
    public async Task Get_event_maps_marks_organizer_attendees_and_stable_id()
    {
        const string json = """
            {
              "id": "ev-1",
              "iCalUId": "uid-1",
              "subject": "Planning",
              "isAllDay": false,
              "isCancelled": false,
              "showAs": "busy",
              "isOrganizer": true,
              "attendees": [ { "emailAddress": { "address": "a@b.c" } } ],
              "start": { "dateTime": "2026-06-15T10:00:00.0000000", "timeZone": "UTC" },
              "end":   { "dateTime": "2026-06-15T11:00:00.0000000", "timeZone": "UTC" },
              "singleValueExtendedProperties": [
                { "id": "String {3D5A8F21-7B4E-4C96-9E1A-D2B6C0F47A83} Name ZmRuleProcessed", "value": "rule-9" }
              ]
            }
            """;
        var snap = await Client(new CapturingHandler(json)).GetEventAsync("ev-1");

        snap.Should().NotBeNull();
        snap!.GraphEventId.Should().Be("ev-1");
        snap.Subject.Should().Be("Planning");
        snap.IsOrganizer.Should().BeTrue();
        snap.HasAttendees.Should().BeTrue();
        snap.HasReplicaMark.Should().BeFalse();
        snap.HasCalImportMark.Should().BeFalse();
        snap.RuleProcessedBy.Should().Be("rule-9");
        snap.StableId.Should().Be(ZyncMaster.Core.OccurrenceId.For(
            "uid-1", new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task List_replicas_in_window_filters_by_the_replica_property_only()
    {
        const string page = """
            {
              "value": [
                {
                  "id": "rep-1",
                  "singleValueExtendedProperties": [
                    { "id": "String {3D5A8F21-7B4E-4C96-9E1A-D2B6C0F47A83} Name ZmReplicaOf", "value": "src-1" }
                  ]
                }
              ]
            }
            """;
        var handler = new CapturingHandler(page);

        var refs = await Client(handler).ListReplicasInWindowAsync(
            "CAL",
            new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero));

        refs.Should().ContainSingle();
        refs[0].EventId.Should().Be("rep-1");
        refs[0].SourceEventId.Should().Be("src-1");
        handler.Urls[0].Should().Contain("ZmReplicaOf",
            "the replica sweep must filter by ITS property — engine separation: it can never " +
            "see (let alone delete) the pair mirror's CalImport events");
        handler.Urls[0].Should().NotContain("CalImportSourceId");
    }

    [Fact]
    public async Task Create_origin_event_carries_body_and_location_only_for_the_origin()
    {
        var handler = new CapturingHandler();
        await Client(handler).CreateOriginEventAsync("CAL", new OriginEventDraft
        {
            Subject = "Dentist",
            BodyHtml = "<p>bring the x-rays</p>",
            Location = "Clinic 4",
            Start = new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 6, 15, 9, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC",
            ShowAs = "busy",
        });

        var body = JObject.Parse(handler.Bodies[0]);
        body["subject"]!.Value<string>().Should().Be("Dentist");
        body["body"]!["content"]!.Value<string>().Should().Contain("x-rays");
        body["location"]!["displayName"]!.Value<string>().Should().Be("Clinic 4");
        body.ContainsKey("singleValueExtendedProperties").Should().BeFalse(
            "an origin event is the USER's real event, not a managed replica — no marks");
    }

    [Fact]
    public async Task Delete_event_swallows_404_so_an_already_gone_replica_is_a_no_op()
    {
        var handler = new CapturingHandler("{}", HttpStatusCode.NotFound);

        var act = () => Client(handler).DeleteEventAsync("gone-ev");

        await act.Should().NotThrowAsync();
    }
}
