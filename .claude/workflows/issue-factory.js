export const meta = {
  name: 'issue-factory',
  description: 'Take a UniGetUI GitHub issue from triage to a reviewed fix, a PR, and Copilot review rounds',
  whenToUse: 'When the user hands over a GitHub issue (number or URL) and wants it fixed end to end',
  phases: [
    { title: 'Triage', detail: 'factory-triage: read the issue, decide whether it is fixable now' },
    { title: 'Fix & Review', detail: 'factory-builder + factory-reviewer, one pass per cycle (Copilot catches the rest; CI runs on GitHub and is not waited on)' },
    { title: 'PR', detail: 'factory-shipper: open the PR, or push a follow-up commit and answer Copilot in-thread' },
    { title: 'Copilot', detail: 'factory-copilot-requester: request a review and collect its comments, up to 3 rounds' },
    { title: 'Cleanup', detail: 'factory-cleanup: delete the worktree, and the local branch once pushed' },
  ],
}

const MAX_LOOPS = 1
const MAX_COPILOT_ROUNDS = 3
const CO_AUTHOR = 'Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>'
const PR_FOOTER = '🤖 Generated with [Claude Code](https://claude.com/claude-code)'

const raw = String(args ?? '').trim()
if (!raw) throw new Error('Pass an issue number, an issue URL, or a description of the bug as args')
const issueMatch = raw.match(/^#?(\d+)$/) || raw.match(/^https?:\/\/github\.com\/[^/\s]+\/[^/\s]+\/issues\/(\d+)\/?(?:[?#]\S*)?$/i)
const N = issueMatch ? issueMatch[1] : null
const REPORT = N ? null : raw
const TAG = N ? `#${N}` : 'report'

const SUBJECT = N
  ? `Bug: Devolutions/UniGetUI issue #${N}.`
  : `Bug: a report pasted by the maintainer (there is no GitHub issue). The report, verbatim:
<<<REPORT
${REPORT}
REPORT>>>`

phase('Triage')
const triage = await agent(`${SUBJECT}
Input kind: ${N ? 'GitHub issue' : 'pasted report'}`, {
  label: `triage ${TAG}`,
  agentType: 'factory-triage',
  schema: {
    type: 'object',
    properties: {
      go: { type: 'boolean' },
      title: { type: 'string' },
      kind: { type: 'string', enum: ['bug', 'enhancement', 'feature', 'question', 'other'] },
      summary: { type: 'string' },
      reason: { type: 'string' },
      repo_root: { type: 'string' },
      slug: { type: 'string' },
      related_issue: { type: 'integer' },
    },
    required: ['go', 'title', 'kind', 'summary', 'reason', 'repo_root'],
  },
})

if (!triage?.go) {
  log(`Stopping: ${TAG} not taken - ${triage?.reason ?? 'triage failed'}`)
  return { issue: N, stage: 'triage', outcome: 'skipped', triage }
}
log(`${TAG} accepted: ${triage.title}`)

const SLUG = N
  ? `issue-${N}`
  : (String(triage.slug ?? '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40).replace(/-+$/, '') || 'bug-report')
const BRANCH = `fix/${SLUG}`
const RELATED = !N && triage.related_issue > 0 ? triage.related_issue : null
const REPO_ROOT = triage.repo_root.replace(/\\/g, '/').replace(/\/+$/, '')
const WT = `${REPO_ROOT}/.claude/worktrees/${SLUG}`
const LINK_LINE = N ? `Fixes #${N}` : RELATED ? `Relates to #${RELATED}` : ''
const PR_LINK = N
  ? `"Closes #${N}"`
  : `a "Bug report" section summarising the maintainer's report (there is no issue)${RELATED ? `, and "Relates to #${RELATED}"` : ''}`

const CONTEXT = `${SUBJECT}
Repo root: ${REPO_ROOT}
Worktree: ${WT}
Branch: ${BRANCH}`

const FIX_SCHEMA = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['fixed', 'no_change', 'not_a_bug', 'blocked'] },
    root_cause: { type: 'string' },
    fix_summary: { type: 'string' },
    files: { type: 'array', items: { type: 'string' } },
    verification: { type: 'string' },
    left_out: { type: 'string' },
    regression_test: {
      type: 'object',
      properties: {
        added: { type: 'boolean' },
        tests: { type: 'array', items: { type: 'string' } },
        fails_without_fix: { type: 'boolean' },
        passes_with_fix: { type: 'boolean' },
        projects_run: { type: 'string' },
        skipped_reason: { type: 'string' },
      },
      required: ['added', 'tests', 'fails_without_fix', 'passes_with_fix', 'projects_run', 'skipped_reason'],
    },
    copilot_dispositions: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          comment_id: { type: 'integer' },
          verdict: { type: 'string', enum: ['fixed', 'fixed_mechanism_corrected', 'declined', 'pre_existing'] },
          explanation: { type: 'string' },
        },
        required: ['comment_id', 'verdict', 'explanation'],
      },
    },
  },
  required: ['status', 'root_cause', 'fix_summary', 'files', 'verification', 'left_out'],
}

const REVIEW_SCHEMA = {
  type: 'object',
  properties: {
    blocking: {
      type: 'array',
      items: {
        type: 'object',
        properties: { file: { type: 'string' }, line: { type: 'integer' }, problem: { type: 'string' }, evidence: { type: 'string' } },
        required: ['file', 'problem', 'evidence'],
      },
    },
    non_blocking: { type: 'array', items: { type: 'string' } },
    verdict: { type: 'string', enum: ['approve', 'changes_requested'] },
  },
  required: ['blocking', 'non_blocking', 'verdict'],
}

const COPILOT_SCHEMA = {
  type: 'object',
  properties: {
    requested: { type: 'boolean' },
    review_received: { type: 'boolean' },
    comments: {
      type: 'array',
      items: {
        type: 'object',
        properties: { id: { type: 'integer' }, path: { type: 'string' }, line: { type: 'integer' }, body: { type: 'string' } },
        required: ['id', 'path', 'body'],
      },
    },
    note: { type: 'string' },
  },
  required: ['requested', 'review_received', 'comments', 'note'],
}

const formatFindings = r => r.blocking
  .map((b, i) => `${i + 1}. ${b.file}${b.line ? ':' + b.line : ''} - ${b.problem}\n   Evidence: ${b.evidence}`)
  .join('\n')

const formatCopilot = comments => comments
  .map(c => `- comment ${c.id}${c.path ? ` at ${c.path}${c.line ? ':' + c.line : ''}` : ' (review body, no thread)'}:\n${c.body.split('\n').map(l => '    ' + l).join('\n')}`)
  .join('\n')

const describeTest = t => !t
  ? 'not reported'
  : t.added
    ? `${t.tests.join(', ') || 'added (names not listed)'} - fails without fix: ${t.fails_without_fix}, passes with fix: ${t.passes_with_fix}; projects run: ${t.projects_run}`
    : `none added: ${t.skipped_reason || 'no reason given'}`

const describeFix = f => `- Root cause: ${f.root_cause}
- Fix: ${f.fix_summary}
- Files: ${f.files.join(', ') || 'not listed'}
- Regression test: ${describeTest(f.regression_test)}
- Deliberately not changed: ${f.left_out}`

const cycleName = cycle => cycle.kind === 'copilot' ? `Copilot round ${cycle.round} of ${MAX_COPILOT_ROUNDS}` : 'initial fix'

const cycleData = (cycle, dispositionsFrom) => {
  if (cycle.kind === 'copilot') return `
PR: ${cycle.prUrl}
Copilot comments this round:
${formatCopilot(cycle.copilot)}${dispositionsFrom ? `\nDispositions so far: ${JSON.stringify(dispositionsFrom.copilot_dispositions ?? [])}` : ''}`
  return ''
}

const initialFixPrompt = () => `${CONTEXT}
Mode: initial
Triage summary: ${triage.summary}${RELATED ? `\nRelated open issue: #${RELATED} - triage found it describes the same bug; read it too.` : ''}`

const roundFixPrompt = cycle => `${CONTEXT}
Mode: ${cycle.kind} (${cycleName(cycle)})
PR: ${cycle.prUrl}
Diagnosis behind the PR, from the previous builder:
${describeFix(cycle.prev)}
${cycleData(cycle).replace(`\nPR: ${cycle.prUrl}`, '')}`

const fixFindingsPrompt = (cycle, loop, prev, reviews) => `${CONTEXT}
Mode: findings - loop ${loop} of ${MAX_LOOPS}, during the ${cycleName(cycle)}.
Previous builder's diagnosis and fix:
${describeFix(prev)}
${cycleData(cycle, prev)}
Blocking findings from the reviewer:
${formatFindings(reviews[reviews.length - 1])}
${reviews.length > 1 ? `\nEarlier rounds' findings, already addressed - do not reintroduce them:\n${reviews.slice(0, -1).map(formatFindings).join('\n')}\n` : ''}
Regression test rules for this round: ${cycle.kind === 'initial' ? 'initial-mode rules (add or rework the test if a finding is about it)' : 'keep the PR test intact and passing'}.`

const reviewPrompt = (cycle, loop, fix, reviews) => `${CONTEXT}
Review round: loop ${loop} of ${MAX_LOOPS}, during the ${cycleName(cycle)}.${cycle.kind === 'initial' ? ' This is the initial round: a missing regression test needs a skipped_reason that holds up.' : ' The diff is only this round\'s delta on top of the PR.'}
Builder's claims (unverified):
${describeFix(fix)}
- Verification: ${fix.verification}
${cycleData(cycle, fix)}${reviews.length ? `\nPrevious review findings the builder says are handled - confirm each:\n${reviews.map(formatFindings).join('\n')}` : ''}`

async function fixReviewCycle(cycle) {
  const tag = cycle.kind === 'initial' ? '' : `${cycle.kind} r${cycle.round} `
  let fix = null
  let review = null
  const reviews = []
  for (let loop = 1; loop <= MAX_LOOPS; loop++) {
    const firstPrompt = cycle.kind === 'initial' ? initialFixPrompt() : roundFixPrompt(cycle)
    fix = await agent(loop === 1 ? firstPrompt : fixFindingsPrompt(cycle, loop, fix, reviews), {
      label: `build ${TAG} ${tag}loop ${loop}`,
      phase: 'Fix & Review',
      agentType: 'factory-builder',
      schema: FIX_SCHEMA,
    })
    if (fix?.status === 'no_change' && cycle.kind !== 'initial' && loop === 1) {
      log(`${tag}builder changed no code: every Copilot comment declined`)
      return { fix, review: null, reviews, approved: true, stopped: false, changed: false }
    }
    if (fix?.status !== 'fixed') {
      log(`Stopping: ${tag}builder in loop ${loop} returned ${fix?.status ?? 'nothing'}`)
      return { fix, review, reviews, approved: false, stopped: true, changed: false }
    }

    review = await agent(reviewPrompt(cycle, loop, fix, reviews), {
      label: `review ${TAG} ${tag}loop ${loop}`,
      phase: 'Fix & Review',
      agentType: 'factory-reviewer',
      schema: REVIEW_SCHEMA,
    })
    if (!review) break
    reviews.push(review)
    log(`${tag}loop ${loop}/${MAX_LOOPS}: ${review.verdict}, ${review.blocking.length} blocking`)
    if (review.verdict === 'approve' && review.blocking.length === 0) break
  }
  const approved = !!review && review.verdict === 'approve' && review.blocking.length === 0
  if (!approved) log(`${tag}loop limit reached without approval; shipping with the open findings listed`)
  return { fix, review, reviews, approved, stopped: false, changed: true }
}

const approvalNote = res => res.approved
  ? 'Approved by the internal reviewer.'
  : res.review
    ? `NOT approved: the internal review loop hit its ${MAX_LOOPS}-round limit. Unresolved review findings to list in the PR body:\n${formatFindings(res.review)}`
    : 'NOT approved: the final internal review did not complete. Say so in the PR body.'

async function finish(result) {
  phase('Cleanup')
  const cleanup = await agent(CONTEXT, {
    label: `cleanup ${TAG}`,
    phase: 'Cleanup',
    agentType: 'factory-cleanup',
    effort: 'low',
    schema: {
      type: 'object',
      properties: {
        removed: { type: 'boolean' },
        branch_deleted: { type: 'boolean' },
        kept_reason: { type: 'string' },
      },
      required: ['removed', 'branch_deleted', 'kept_reason'],
    },
  })
  log(cleanup?.removed ? `Worktree ${WT} deleted` : `Worktree kept: ${cleanup?.kept_reason ?? 'cleanup agent failed'}`)
  return { ...result, worktree: cleanup?.removed ? null : WT, cleanup }
}

phase('Fix & Review')
const first = await fixReviewCycle({ kind: 'initial', round: 0 })
if (first.stopped) {
  return await finish({ issue: N, stage: 'fix', outcome: first.fix?.status ?? 'failed', branch: BRANCH, triage, cycles: [first] })
}

phase('PR')
const pr = await agent(`${CONTEXT}
Mode: open
Review: ${approvalNote(first)}
Commit link line: ${LINK_LINE || 'none'}
PR link requirement: ${PR_LINK}
Root cause: ${first.fix.root_cause}
Fix: ${first.fix.fix_summary}
Verification: ${first.fix.verification}
Not covered: ${first.fix.left_out}
Regression test: ${describeTest(first.fix.regression_test)}
Non-blocking review notes: ${first.review?.non_blocking.join(' ; ') || 'none'}
Co-author line: ${CO_AUTHOR}
PR footer: ${PR_FOOTER}`, {
  label: `open PR ${TAG}`,
  agentType: 'factory-shipper',
  schema: {
    type: 'object',
    properties: { pr_url: { type: 'string' }, pr_number: { type: 'integer' }, commit: { type: 'string' }, notes: { type: 'string' } },
    required: ['pr_url', 'pr_number', 'commit', 'notes'],
  },
})

if (!pr || !(pr.pr_number > 0) || !/^https:\/\/github\.com\//.test(pr.pr_url ?? '')) {
  log(`No PR was opened: ${pr?.notes ?? 'shipper agent failed'}`)
  return await finish({ issue: N, stage: 'pr', outcome: 'pr_failed', branch: BRANCH, triage, cycles: [first], shipper: pr })
}
log(`PR opened: ${pr.pr_url}`)

const P = pr.pr_number
const copilotRounds = []
let lastFix = first.fix
let copilotOutcome = 'round_limit'

for (let round = 1; round <= MAX_COPILOT_ROUNDS; round++) {
  phase('Copilot')
  const cop = await agent(`${CONTEXT}
PR: #${P} (${pr.pr_url})
Copilot round: ${round} of ${MAX_COPILOT_ROUNDS}`, {
    label: `copilot review #${P} r${round}`,
    phase: 'Copilot',
    agentType: 'factory-copilot-requester',
    schema: COPILOT_SCHEMA,
  })

  if (!cop?.requested) { copilotOutcome = 'not_requested'; log(`Copilot review could not be requested: ${cop?.note ?? 'agent failed'}`); copilotRounds.push({ round, cop }); break }
  if (!cop.review_received) { copilotOutcome = 'timed_out'; log(`No Copilot review arrived in round ${round}: ${cop.note}`); copilotRounds.push({ round, cop }); break }
  if (cop.comments.length === 0) { copilotOutcome = 'clean'; log(`Copilot round ${round}: no comments`); copilotRounds.push({ round, cop }); break }
  log(`Copilot round ${round}: ${cop.comments.length} comment(s), back to the builder`)

  phase('Fix & Review')
  const cycle = { kind: 'copilot', round, copilot: cop.comments, prev: lastFix, prUrl: pr.pr_url }
  const res = await fixReviewCycle(cycle)
  if (res.stopped) {
    copilotRounds.push({ round, cop, res })
    return await finish({ issue: N, stage: 'copilot_fix', outcome: 'pr_opened_followup_blocked', pr, branch: BRANCH, triage, cycles: [first], copilotRounds })
  }

  phase('PR')
  const update = await agent(`${CONTEXT}
Mode: copilot
PR: #${P} (${pr.pr_url})
Code change: ${res.changed ? `yes, uncommitted in the worktree. ${approvalNote(res)}` : 'none - the builder declined every comment; reply and resolve only.'}
Copilot comments:
${formatCopilot(cop.comments)}
Builder's dispositions (comment_id -> verdict, evidence): ${JSON.stringify(res.fix.copilot_dispositions ?? [])}
Builder's summary: ${res.fix.fix_summary}
Co-author line: ${CO_AUTHOR}`, {
    label: `push + reply #${P} r${round}`,
    phase: 'PR',
    agentType: 'factory-shipper',
    schema: {
      type: 'object',
      properties: { commit: { type: 'string' }, pushed: { type: 'boolean' }, replies: { type: 'integer' }, resolved: { type: 'integer' }, notes: { type: 'string' } },
      required: ['commit', 'pushed', 'replies', 'resolved', 'notes'],
    },
  })
  copilotRounds.push({ round, cop, res, update })
  const threaded = cop.comments.filter(c => c.id > 0).length
  const missing = [
    res.changed && !update?.pushed ? 'the fix commit is not pushed' : '',
    (update?.replies ?? 0) < threaded ? `${update?.replies ?? 0} of ${threaded} Copilot comments answered` : '',
    (update?.resolved ?? 0) < threaded ? `${update?.resolved ?? 0} of ${threaded} threads resolved` : '',
  ].filter(Boolean)
  if (missing.length) {
    log(`Stopping Copilot rounds: ${missing.join('; ')}. ${update?.notes ?? 'push agent failed'}`)
    return await finish({ issue: N, slug: SLUG, stage: 'copilot_ship', outcome: 'pr_opened_followup_incomplete', pr, checks: `${pr.pr_url}/checks`, missing, shipperNotes: update?.notes, approved: first.approved, copilotRounds, branch: BRANCH, triage, fix: res.fix })
  }
  if (!res.changed) { copilotOutcome = 'all_declined'; log(`Copilot round ${round}: every comment declined and answered; no code changed, so no new Copilot review`); break }
  lastFix = res.fix
  if (round === MAX_COPILOT_ROUNDS) log(`Copilot round limit reached; round ${round} fixes are pushed and answered, no further Copilot review requested`)
}

return await finish({
  issue: N,
  slug: SLUG,
  stage: 'done',
  outcome: 'pr_opened',
  pr,
  checks: `${pr.pr_url}/checks`,
  approved: first.approved,
  loops: first.reviews.length,
  copilotOutcome,
  copilotRounds: copilotRounds.map(c => ({
    round: c.round,
    comments: c.cop?.comments?.length ?? 0,
    approved: c.res?.approved ?? null,
    commit: c.update?.commit ?? '',
    replies: c.update?.replies ?? 0,
    resolved: c.update?.resolved ?? 0,
  })),
  branch: BRANCH,
  triage,
  fix: lastFix,
})
