namespace Kyuplan.Client.ViewModels;

/// <summary>The example script the settings offer for the custom data source: it shows the two functions and what they return.</summary>
public static class CustomScriptTemplate
{
    public const string Text = """
        // The two functions below are called by the API. What they return is kept for a few minutes.
        // http.get(url, headers) / http.post(url, body, headers) answer { status, ok, text, json() }; console.log(...) writes to the API's log.

        var base = 'https://example.org/api';
        var auth = { Authorization: 'Bearer YOUR-TOKEN' };

        // All groups with their subgroups and members. birthDate is optional ("2012-05-31").
        function getGroups() {
          var groups = http.get(base + '/groups', auth).json();
          return groups.map(function (g) {
            return {
              id: g.id,
              name: g.name,
              subGroups: g.subGroups.map(function (s) { return { id: s.id, name: s.name }; }),
              members: g.members.map(function (m) {
                return { id: m.id, name: m.name, subGroupIds: m.subGroupIds, birthDate: m.birthDate };
              })
            };
          });
        }

        // The events of one group that end between from and to (ISO 8601 texts).
        // responses has an entry for every invited member: accepted, declined, unanswered or other.
        function getEvents(groupId, from, to) {
          var events = http.get(base + '/groups/' + groupId + '/events?from=' + from + '&to=' + to, auth).json();
          return events.map(function (e) {
            var responses = {};
            e.invitations.forEach(function (i) { responses[i.memberId] = i.answer; });
            return { id: e.id, name: e.title, start: e.start, end: e.end, cancelled: e.cancelled, responses: responses };
          });
        }
        """;
}
