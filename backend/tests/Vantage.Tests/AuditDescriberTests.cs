using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>The one-line descriptions shown in the Audit Log.</summary>
public class AuditDescriberTests
{
    [Fact]
    public void Members_added_names_the_group_dashboard_and_ticket()
    {
        var text = AuditDescriber.Describe("group.members-added", "DashboardGroup", "7",
            """{"group":"SALES-North","count":5,"moved":1,"emails":["a@rrd.com","b@rrd.com","c@rrd.com","d@rrd.com","e@rrd.com"]}""", "SALES-North", "Sales Overview", "INC0042");
        Assert.Equal("Added 5 people to group SALES-North on Sales Overview (1 moved from another group): a@rrd.com, b@rrd.com, c@rrd.com and others. Ticket INC0042.", text);
    }

    [Fact]
    public void Role_change_shows_before_and_after()
    {
        var text = AuditDescriber.Describe("user.updated", "User", "3", """{"rolesBefore":["Dashboard User"],"rolesAfter":["Dashboard User","Dashboard Owner"]}""", "priya@rrd.com", null, null);
        Assert.Equal("Changed roles of priya@rrd.com: Dashboard User → Dashboard User, Dashboard Owner.", text);
    }

    [Fact]
    public void Unknown_action_falls_back_to_a_readable_sentence_and_bad_json_is_ignored()
    {
        Assert.Equal("Something happened: Sales Overview.", AuditDescriber.Describe("dashboard.something-happened", "Dashboard", "1", "not json", null, "Sales Overview", null));
    }

    [Fact]
    public void Failed_tenant_check_lists_the_failed_checks()
    {
        var text = AuditDescriber.Describe("tenant.verified", "BiTenant", "2", """{"Passed":false,"failed":["Workspace access"]}""", "Prod PBI", null, null);
        Assert.Equal("Verified tenant Prod PBI: some checks failed (Workspace access).", text);
    }

    [Fact]
    public void Approval_requests_and_overrides_are_described_with_their_reason()
    {
        Assert.Equal("Asked the owners to approve adding 10 people to group SALES-North on Sales Overview: a@rrd.com, b@rrd.com, c@rrd.com and others.",
            AuditDescriber.Describe("group.add-requested", "DashboardGroup", "7", """{"group":"SALES-North","count":10,"emails":["a@rrd.com","b@rrd.com","c@rrd.com","d@rrd.com"]}""", "SALES-North", "Sales Overview", null));
        Assert.Equal("The owners approved adding 8 of 10 people to group SALES-North on Sales Overview, rejected 2.",
            AuditDescriber.Describe("group.add-approved", "DashboardGroup", "7", """{"group":"SALES-North","approved":8,"rejected":2,"total":10}""", null, "Sales Overview", null));
        Assert.Equal("A Super Admin approved in place of the owners adding 1 of 1 person to group G on D. Reason: Owner on leave.",
            AuditDescriber.Describe("group.add-approved", "DashboardGroup", "7", """{"group":"G","approved":1,"rejected":0,"total":1,"overrideReason":"Owner on leave"}""", null, "D", null));
        Assert.Contains("without the owners' approval, reason: Outage",
            AuditDescriber.Describe("group.members-added", "DashboardGroup", "7", """{"group":"G","count":1,"emails":["a@rrd.com"],"overrideReason":"Outage"}""", null, "D", null));
    }
}
