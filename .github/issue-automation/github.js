"use strict";

function createGitHubClient({ repo, token, fetchImpl = fetch }) {
  if (!repo || !repo.includes("/")) throw new Error("GITHUB_REPOSITORY is required");
  if (!token) throw new Error("GITHUB_TOKEN is required");
  const baseUrl = `https://api.github.com/repos/${repo}`;

  async function request(method, path, body = undefined) {
    const response = await fetchImpl(`${baseUrl}${path}`, {
      method,
      headers: {
        Accept: "application/vnd.github+json",
        Authorization: `Bearer ${token}`,
        "X-GitHub-Api-Version": "2022-11-28",
        ...(body ? { "Content-Type": "application/json" } : {}),
      },
      body: body ? JSON.stringify(body) : undefined,
    });
    if (!response.ok) {
      throw new Error(`GitHub API ${method} ${path} failed with ${response.status}`);
    }
    return response.status === 204 ? null : response.json();
  }

  async function paginate(path, maxPages = 10) {
    const items = [];
    for (let page = 1; page <= maxPages; page += 1) {
      const sep = path.includes("?") ? "&" : "?";
      const batch = await request("GET", `${path}${sep}per_page=100&page=${page}`);
      items.push(...batch);
      if (batch.length < 100) break;
    }
    return items;
  }

  return {
    request,
    paginate,
    getIssue: (number) => request("GET", `/issues/${number}`),
    listIssueComments: (number) => paginate(`/issues/${number}/comments`, 3),
    listIssueEvents: (number) => paginate(`/issues/${number}/events`, 3),
    listOpenIssuesAndPrs: (maxPages = 6) => paginate("/issues?state=open&sort=updated&direction=desc", maxPages),
    addLabels: (number, labels) => request("POST", `/issues/${number}/labels`, { labels }),
  };
}

module.exports = {
  createGitHubClient,
};
