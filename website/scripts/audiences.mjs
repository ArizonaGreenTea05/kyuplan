// Which file of ../docs (or the repository root) is a page of which audience's site. The audiences are the ones of Kyuplan.Contracts/Documentation.cs
// (user, admin, deployment: served by the API to the users who may read them) plus "developer", which only the release's documentation archive carries.
// Per file: its path from the repository root and the name of the generated page. The first file of an audience is its start page.
export const audiences = {
  user: {
    title: 'User guide',
    files: [
      { source: 'docs/user-guide.md', page: 'user-guide' },
      { source: 'docs/attendance-statistics.md', page: 'attendance-statistics' },
      { source: 'docs/exam-planning.md', page: 'exam-planning' },
      { source: 'docs/comments-and-tags.md', page: 'comments-and-tags' },
    ],
  },
  admin: {
    title: 'Administrator guide',
    files: [{ source: 'docs/admin-guide.md', page: 'admin-guide' }],
  },
  deployment: {
    title: 'Deployment guide',
    files: [{ source: 'docs/deployment.md', page: 'deployment' }],
  },
  developer: {
    title: 'Developer guide',
    files: [
      { source: 'docs/developer-guide.md', page: 'developer-guide' },
      { source: 'docs/reference.md', page: 'reference' },
    ],
  },
};
