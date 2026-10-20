"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const OpenAI = require("openai");
const {
  APIConnectionError, APIConnectionTimeoutError, APIUserAbortError,
} = OpenAI;

const {
  AgentFailure, TOOLS, compileOutputValidator, executeTool, providerFailureDiagnostic,
  providerFailureReason, runAgent,
} = require("../src/agent");
const { MAX_STREAMED_MODEL_DATA_BYTES } = require("../src/limits");
const { RuntimeMetrics, createProviderClient, sanitizeReason } = require("../src/provider");

const schema = {
  type: "object",
  additionalProperties: false,
  required: ["answer"],
  properties: { answer: { type: "string" } },
};

const baseConfig = {
  id: "test",
  model: "primary",
  prompt_file: "prompt",
  schema_file: "schema",
  methodology_files: [],
  allowed_roots: [],
  allowed_files: [],
  max_turns: 4,
  max_tool_calls: 4,
  max_request_retries: 0,
};

const sandbox = {
  readFile: (args) => JSON.stringify({ ok: true, read: args.path }),
  listFiles: (args) => JSON.stringify({ ok: true, listed: args.path }),
  searchText: (args) => JSON.stringify({ ok: true, searched: args.query }),
};

function message(content, toolCalls) {
  return { choices: [{ message: {
    role: "assistant",
    content,
    ...(toolCalls ? { tool_calls: toolCalls } : {}),
  } }] };
}

function call(id, name, args) {
  return {
    id,
    type: "function",
    function: { name, arguments: typeof args === "string" ? args : JSON.stringify(args) },
  };
}

function completionStream(response, { fragments = null, usage = undefined } = {}) {
  const controller = new AbortController();
  return {
    controller,
    async *[Symbol.asyncIterator]() {
      if (fragments) {
        for (const fragment of fragments) yield fragment;
        return;
      }
      const choice = response?.choices?.[0];
      const source = choice?.message;
      const toolCalls = Array.isArray(source?.tool_calls)
        ? source.tool_calls.map((toolCall, index) => ({ index, ...toolCall }))
        : undefined;
      yield {
        choices: [{
          index: 0,
          delta: {
            ...(source?.content === undefined ? {} : { content: source.content }),
            ...(source?.reasoning_content === undefined
              ? {}
              : { reasoning_content: source.reasoning_content }),
            ...(toolCalls === undefined ? {} : { tool_calls: toolCalls }),
          },
          finish_reason: toolCalls?.length ? "tool_calls" : "stop",
        }],
      };
      if (usage !== undefined) yield { choices: [], usage };
    },
  };
}

function sseResponse(content, { headers = {}, usage = undefined } = {}) {
  const events = [{
    id: "completion",
    object: "chat.completion.chunk",
    created: 0,
    model: "test",
    choices: [{
      index: 0,
      delta: { content },
      finish_reason: "stop",
    }],
  }];
  if (usage !== undefined) {
    events.push({
      id: "completion",
      object: "chat.completion.chunk",
      created: 0,
      model: "test",
      choices: [],
      usage,
    });
  }
  const body = `${events.map((event) => `data: ${JSON.stringify(event)}\n\n`).join("")}data: [DONE]\n\n`;
  return new Response(body, {
    status: 200,
    headers: { "content-type": "text/event-stream", ...headers },
  });
}

function clientFrom(sequence, requests = []) {
  return {
    chat: {
      completions: {
        async create(request) {
          requests.push(structuredClone(request));
          const next = sequence.shift();
          if (next instanceof Error || next?.throw) throw next.throw || next;
          const response = typeof next === "function" ? await next(request) : next;
          return response?.[Symbol.asyncIterator] ? response : completionStream(response);
        },
      },
    },
  };
}

test("runtime executes only declared tools and returns schema-validated canonical JSON", async () => {
  const requests = [];
  const client = clientFrom([
    message(null, [
      call("one", "read_file", { path: "root/a" }),
      call("two", "list_files", { path: "root" }),
      call("three", "search_text", { path: "root", query: "needle" }),
    ]),
    message('{ "answer": "done" }'),
  ], requests);
  const result = await runAgent({
    client, config: baseConfig, methodologies: ["method"], prompt: "prompt", sandbox, schema,
  });
  assert.deepEqual(result, {
    output: '{"answer":"done"}',
    turnCount: 2,
    toolCallCount: 3,
    outputRepairCount: 0,
  });
  assert.deepEqual(requests[0].tools, TOOLS);
  assert.deepEqual(TOOLS.map((tool) => tool.function.name), [
    "read_file", "list_files", "search_text",
  ]);
  assert.equal(requests[0].tool_choice, "auto");
  assert.equal(requests[0].parallel_tool_calls, false);
  assert.match(requests[0].messages[0].content, /required output JSON Schema/);
  assert.match(requests[0].messages[0].content, /"answer"/);
  assert.deepEqual(requests[1].messages.slice(-3).map((entry) => entry.tool_call_id), [
    "one", "two", "three",
  ]);
});

test("runtime assembles streamed reasoning, content, usage, and request controls", async () => {
  const requests = [];
  const metrics = new RuntimeMetrics();
  const fragments = [
    {
      choices: [{
        index: 0,
        delta: { role: "assistant", reasoning_content: "checked ", content: '{"ans' },
        finish_reason: null,
      }],
    },
    {
      choices: [{
        index: 0,
        delta: { reasoning_content: "carefully", content: 'wer":"done"}' },
        finish_reason: "stop",
      }],
    },
    {
      choices: [],
      usage: { prompt_tokens: 5, completion_tokens: 3, total_tokens: 8 },
    },
  ];
  const stream = completionStream(null, { fragments });
  const result = await runAgent({
    client: {
      chat: {
        completions: {
          async create(request) {
            requests.push(structuredClone(request));
            metrics.beginAttempt();
            return stream;
          },
        },
      },
    },
    config: { ...baseConfig, max_tool_calls: 0 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
    metrics,
  });

  assert.equal(result.output, '{"answer":"done"}');
  assert.equal(requests[0].stream, true);
  assert.deepEqual(requests[0].stream_options, { include_usage: true });
  assert.equal(requests[0].reasoning_effort, "high");
  assert.equal(requests[0].max_tokens, undefined);
  assert.deepEqual(metrics.snapshot().tokenUsage, {
    complete: true,
    knownAttemptCount: 1,
    unknownAttemptCount: 0,
    inputTokens: 5,
    outputTokens: 3,
    totalTokens: 8,
  });
  assert.equal(metrics.snapshot().ignoredPostFinishEmptyDeltaChoices, 0);
  assert.equal(metrics.snapshot().ignoredRepeatedTerminalChoices, 0);
});

test("runtime passes a stage-derived SDK timeout above 120 seconds", async () => {
  let options;
  const result = await runAgent({
    client: {
      chat: {
        completions: {
          async create(_request, requestOptions) {
            options = requestOptions;
            return completionStream(message('{"answer":"done"}'));
          },
        },
      },
    },
    config: { ...baseConfig, max_tool_calls: 0, stage_timeout_ms: 180_000 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
  });

  assert.equal(result.output, '{"answer":"done"}');
  assert.ok(options.timeout > 120_000);
  assert.ok(options.timeout <= 180_000);
});

test("runtime accepts an omitted choice index in a single-choice chunk", async () => {
  const metrics = new RuntimeMetrics();
  const emptyNullPrototypeDelta = Object.create(null);
  const result = await runAgent({
    client: clientFrom([completionStream(null, {
      fragments: [
        {
          choices: [{
            delta: { content: '{"answer":"done"}' },
            finish_reason: "stop",
          }],
        },
        { choices: [{ delta: emptyNullPrototypeDelta }] },
      ],
    })]),
    config: { ...baseConfig, max_tool_calls: 0 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
    metrics,
  });

  assert.equal(result.output, '{"answer":"done"}');
  assert.equal(metrics.snapshot().ignoredPostFinishEmptyDeltaChoices, 1);
  assert.equal(metrics.snapshot().ignoredRepeatedTerminalChoices, 0);
});

test("runtime accepts one complete index-less tool call before a terminal chunk", async () => {
  const executions = [];
  const result = await runAgent({
    client: clientFrom([
      completionStream(null, {
        fragments: [
          {
            choices: [{
              delta: {
                tool_calls: [{
                  id: "call-1",
                  type: "function",
                  function: { name: "read_file", arguments: '{"path":"root/a"}' },
                }],
              },
              finish_reason: null,
            }],
          },
          { choices: [{ delta: {}, finish_reason: "tool_calls" }] },
        ],
      }),
      message('{"answer":"done"}'),
    ]),
    config: baseConfig,
    methodologies: [],
    prompt: "p",
    sandbox: {
      ...sandbox,
      readFile(args) {
        executions.push(args.path);
        return JSON.stringify({ ok: true });
      },
    },
    schema,
  });

  assert.equal(result.output, '{"answer":"done"}');
  assert.deepEqual(executions, ["root/a"]);
});

test("runtime preserves a usage-only post-finish tail before executing a completed tool call", async () => {
  const executions = [];
  const metrics = new RuntimeMetrics();
  let streamCompleted = false;
  const toolStream = {
    controller: new AbortController(),
    async *[Symbol.asyncIterator]() {
      yield {
        choices: [{
          index: 0,
          delta: {
            tool_calls: [{
              index: 0,
              id: "call-1",
              type: "function",
              function: { name: "read_file", arguments: '{"path":"' },
            }],
          },
          finish_reason: null,
        }],
      };
      yield {
        choices: [{
          index: 0,
          delta: {
            tool_calls: [{
              index: 0,
              function: { arguments: 'root/a"}' },
            }],
          },
          finish_reason: "tool_calls",
        }],
      };
      yield {
        choices: [],
        usage: { prompt_tokens: 5, completion_tokens: 3, total_tokens: 8 },
      };
      streamCompleted = true;
    },
  };
  const client = clientFrom([
    toolStream,
    message('{"answer":"done"}'),
  ]);
  const create = client.chat.completions.create;
  client.chat.completions.create = async (request) => {
    metrics.beginAttempt();
    return create(request);
  };
  const result = await runAgent({
    client,
    config: baseConfig,
    methodologies: [],
    prompt: "p",
    sandbox: {
      ...sandbox,
      readFile(args) {
        assert.equal(streamCompleted, true, "tool execution must wait for stream completion");
        executions.push(args.path);
        return JSON.stringify({ ok: true });
      },
    },
    schema,
    metrics,
  });

  assert.equal(result.output, '{"answer":"done"}');
  assert.deepEqual(executions, ["root/a"]);
  assert.equal(metrics.snapshot().ignoredPostFinishEmptyDeltaChoices, 0);
  assert.equal(metrics.snapshot().ignoredRepeatedTerminalChoices, 0);
  assert.deepEqual(metrics.snapshot().tokenUsage, {
    complete: false,
    knownAttemptCount: 1,
    unknownAttemptCount: 1,
    inputTokens: 5,
    outputTokens: 3,
    totalTokens: 8,
  });
});

test("runtime ignores and counts a repeated terminal with an empty delta", async () => {
  const metrics = new RuntimeMetrics();
  const result = await runAgent({
    client: clientFrom([completionStream(null, {
      fragments: [
        {
          choices: [{
            index: 0,
            delta: { content: '{"answer":"done"}' },
            finish_reason: "stop",
          }],
        },
        { choices: [{ index: 0, delta: {}, finish_reason: "stop" }] },
      ],
    })]),
    config: { ...baseConfig, max_tool_calls: 0 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
    metrics,
  });

  assert.equal(result.output, '{"answer":"done"}');
  assert.equal(metrics.snapshot().ignoredPostFinishEmptyDeltaChoices, 1);
  assert.equal(metrics.snapshot().ignoredRepeatedTerminalChoices, 1);
});

test("runtime accepts a no-op post-finish tail with an explicit null finish reason", async () => {
  const metrics = new RuntimeMetrics();
  const result = await runAgent({
    client: clientFrom([completionStream(null, {
      fragments: [
        {
          choices: [{
            index: 0,
            delta: { content: '{"answer":"done"}' },
            finish_reason: "stop",
          }],
        },
        { choices: [{ index: 0, delta: {}, finish_reason: null }] },
      ],
    })]),
    config: { ...baseConfig, max_tool_calls: 0 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
    metrics,
  });

  assert.equal(result.output, '{"answer":"done"}');
  assert.equal(metrics.snapshot().ignoredPostFinishEmptyDeltaChoices, 1);
  assert.equal(metrics.snapshot().ignoredRepeatedTerminalChoices, 0);
});

test("runtime continues to assemble indexed interleaved tool fragments before executing the whole batch", async () => {
  const requests = [];
  const executions = [];
  const guardedSandbox = {
    readFile(args) {
      executions.push(["read", args.path]);
      return JSON.stringify({ ok: true });
    },
    listFiles(args) {
      executions.push(["list", args.path]);
      return JSON.stringify({ ok: true });
    },
    searchText() {
      throw new Error("unexpected search");
    },
  };
  const toolStream = {
    controller: new AbortController(),
    async *[Symbol.asyncIterator]() {
      yield {
        choices: [{
          index: 0,
          delta: {
            reasoning_content: "inspect",
            tool_calls: [{
              index: 1,
              id: "second",
              type: "function",
              function: { name: "list_files", arguments: '{"pa' },
            }],
          },
          finish_reason: null,
        }],
      };
      assert.deepEqual(executions, []);
      yield {
        choices: [{
          index: 0,
          delta: {
            tool_calls: [
              {
                index: 0,
                id: "first",
                type: "function",
                function: { name: "read_file", arguments: '{"path":"a' },
              },
              {
                index: 1,
                function: { arguments: 'th":"root"}' },
              },
            ],
          },
          finish_reason: null,
        }],
      };
      assert.deepEqual(executions, []);
      yield {
        choices: [{
          index: 0,
          delta: {
            tool_calls: [{
              index: 0,
              function: { arguments: '"}' },
            }],
          },
          finish_reason: "tool_calls",
        }],
      };
    },
  };
  const result = await runAgent({
    client: clientFrom([toolStream, message('{"answer":"done"}')], requests),
    config: baseConfig,
    methodologies: [],
    prompt: "p",
    sandbox: guardedSandbox,
    schema,
  });

  assert.equal(result.output, '{"answer":"done"}');
  assert.deepEqual(executions, [["read", "a"], ["list", "root"]]);
  assert.equal(requests[1].messages.at(-2).tool_call_id, "first");
  assert.equal(requests[1].messages.at(-1).tool_call_id, "second");
  assert.equal(requests[1].messages.at(-3).reasoning_content, "inspect");
});

test("runtime replaces repeated indexed tool identity snapshots and appends arguments", async () => {
  const executions = [];
  const requests = [];
  const result = await runAgent({
    client: clientFrom([
      completionStream(null, {
        fragments: [
          {
            choices: [{
              index: 0,
              delta: {
                tool_calls: [{
                  index: 0,
                  id: "call-1",
                  type: "function",
                  function: { name: "read_file", arguments: '{"path":"' },
                }],
              },
              finish_reason: null,
            }],
          },
          {
            choices: [{
              index: 0,
              delta: {
                tool_calls: [{
                  index: 0,
                  id: "call-1",
                  type: "function",
                  function: { name: "read_file", arguments: 'root/a"}' },
                }],
              },
              finish_reason: "tool_calls",
            }],
          },
        ],
      }),
      message('{"answer":"done"}'),
    ], requests),
    config: baseConfig,
    methodologies: [],
    prompt: "p",
    sandbox: {
      ...sandbox,
      readFile(args) {
        executions.push(args.path);
        return JSON.stringify({ ok: true });
      },
    },
    schema,
  });

  assert.equal(result.output, '{"answer":"done"}');
  assert.deepEqual(executions, ["root/a"]);
  assert.equal(requests[1].messages.at(-1).tool_call_id, "call-1");
  assert.deepEqual(requests[1].messages.at(-2).tool_calls, [{
    id: "call-1",
    type: "function",
    function: { name: "read_file", arguments: '{"path":"root/a"}' },
  }]);
});

test("runtime retains the latest truthy indexed tool identity snapshots", async () => {
  const executions = [];
  const requests = [];
  const result = await runAgent({
    client: clientFrom([
      completionStream(null, {
        fragments: [
          {
            choices: [{
              index: 0,
              delta: {
                tool_calls: [{
                  index: 0,
                  id: "call-old",
                  type: "not-a-function",
                  function: { name: "unknown_tool", arguments: '{"path":"' },
                }],
              },
              finish_reason: null,
            }],
          },
          {
            choices: [{
              index: 0,
              delta: {
                tool_calls: [{
                  index: 0,
                  id: "call-new",
                  type: "function",
                  function: { name: "read_file", arguments: 'root/a"}' },
                }],
              },
              finish_reason: "tool_calls",
            }],
          },
        ],
      }),
      message('{"answer":"done"}'),
    ], requests),
    config: baseConfig,
    methodologies: [],
    prompt: "p",
    sandbox: {
      ...sandbox,
      readFile(args) {
        executions.push(args.path);
        return JSON.stringify({ ok: true });
      },
    },
    schema,
  });

  assert.equal(result.output, '{"answer":"done"}');
  assert.deepEqual(executions, ["root/a"]);
  assert.equal(requests[1].messages.at(-1).tool_call_id, "call-new");
  assert.deepEqual(requests[1].messages.at(-2).tool_calls, [{
    id: "call-new",
    type: "function",
    function: { name: "read_file", arguments: '{"path":"root/a"}' },
  }]);
});

test("runtime retains truthy indexed tool identities when later fragments omit them", async () => {
  const executions = [];
  const result = await runAgent({
    client: clientFrom([
      completionStream(null, {
        fragments: [
          {
            choices: [{
              index: 0,
              delta: {
                tool_calls: [{
                  index: 0,
                  id: "call-1",
                  type: "function",
                  function: { name: "read_file", arguments: '{"path":"' },
                }],
              },
              finish_reason: null,
            }],
          },
          {
            choices: [{
              index: 0,
              delta: {
                tool_calls: [{
                  index: 0,
                  type: null,
                  function: { name: "", arguments: 'root/a"}' },
                }],
              },
              finish_reason: "tool_calls",
            }],
          },
        ],
      }),
      message('{"answer":"done"}'),
    ]),
    config: baseConfig,
    methodologies: [],
    prompt: "p",
    sandbox: {
      ...sandbox,
      readFile(args) {
        executions.push(args.path);
        return JSON.stringify({ ok: true });
      },
    },
    schema,
  });

  assert.equal(result.output, '{"answer":"done"}');
  assert.deepEqual(executions, ["root/a"]);
});

test("runtime rejects final indexed tool calls with an absent or empty ID or non-function type", async () => {
  const cases = [
    {
      name: "absent ID",
      toolCall: { index: 0, type: "function", function: { name: "read_file", arguments: "{}" } },
    },
    {
      name: "empty ID",
      toolCall: { index: 0, id: "", type: "function", function: { name: "read_file", arguments: "{}" } },
    },
    {
      name: "non-function type",
      toolCall: { index: 0, id: "call-1", type: "tool", function: { name: "read_file", arguments: "{}" } },
    },
  ];

  for (const streamCase of cases) {
    let executions = 0;
    await assert.rejects(
      runAgent({
        client: clientFrom([completionStream(null, {
          fragments: [{
            choices: [{
              index: 0,
              delta: { tool_calls: [streamCase.toolCall] },
              finish_reason: "tool_calls",
            }],
          }],
        })]),
        config: baseConfig,
        methodologies: [],
        prompt: "p",
        sandbox: {
          ...sandbox,
          readFile() {
            executions++;
            return "{}";
          },
        },
        schema,
      }),
      (error) => error.reason === "provider returned a malformed tool call",
      streamCase.name,
    );
    assert.equal(executions, 0, streamCase.name);
  }
});

test("runtime rejects non-string indexed tool identity snapshots as malformed streams", async () => {
  const cases = [
    {
      name: "ID",
      toolCall: { index: 0, id: 1, type: "function", function: { name: "read_file", arguments: "{}" } },
    },
    {
      name: "type",
      toolCall: { index: 0, id: "call-1", type: 1, function: { name: "read_file", arguments: "{}" } },
    },
    {
      name: "function name",
      toolCall: { index: 0, id: "call-1", type: "function", function: { name: 1, arguments: "{}" } },
    },
  ];

  for (const streamCase of cases) {
    const metrics = new RuntimeMetrics();
    let executions = 0;
    await assert.rejects(
      runAgent({
        client: clientFrom([completionStream(null, {
          fragments: [{
            choices: [{
              index: 0,
              delta: { tool_calls: [streamCase.toolCall] },
              finish_reason: "tool_calls",
            }],
          }],
        })]),
        config: baseConfig,
        methodologies: [],
        prompt: "p",
        sandbox: {
          ...sandbox,
          readFile() {
            executions++;
            return "{}";
          },
        },
        schema,
        metrics,
      }),
      (error) => error.reason === "provider stream was malformed",
      streamCase.name,
    );
    assert.equal(executions, 0, streamCase.name);
    assert.equal(metrics.snapshot().streamStructuralViolation, "delta-value-invalid", streamCase.name);
  }
});

test("runtime counts repeated indexed tool identity snapshots against streamed model data", async () => {
  const identity = "x".repeat(Math.ceil(MAX_STREAMED_MODEL_DATA_BYTES / 2));
  let executions = 0;
  await assert.rejects(
    runAgent({
      client: clientFrom([completionStream(null, {
        fragments: [
          {
            choices: [{
              index: 0,
              delta: {
                tool_calls: [{
                  index: 0,
                  id: identity,
                  type: "function",
                  function: { name: "read_file", arguments: "{}" },
                }],
              },
              finish_reason: null,
            }],
          },
          {
            choices: [{
              index: 0,
              delta: { tool_calls: [{ index: 0, id: identity }] },
              finish_reason: "tool_calls",
            }],
          },
        ],
      })]),
      config: baseConfig,
      methodologies: [],
      prompt: "p",
      sandbox: {
        ...sandbox,
        readFile() {
          executions++;
          return "{}";
        },
      },
      schema,
    }),
    (error) => error.reason === "streamed model data exceeded the byte limit" &&
      error.category === "limit",
  );
  assert.equal(executions, 0);
});

test("runtime rejects incompatible streamed indices without executing tools or exposing provider data", async () => {
  const sentinel = "MODEL_STREAM_SECRET_SENTINEL";
  const fullCall = () => ({
    id: `${sentinel}_ID`,
    type: "function",
    function: { name: "read_file", arguments: `{"path":"${sentinel}_ARGUMENTS"}` },
  });
  const cases = [
    {
      name: "partial index-less tool call",
      violation: "tool-call-index-missing-fragmented",
      fragments: [{
        choices: [{
          index: 0,
          delta: {
            tool_calls: [{
              id: `${sentinel}_ID`,
              type: "function",
              function: { name: "read_file" },
            }],
          },
          finish_reason: "tool_calls",
        }],
      }],
    },
    {
      name: "index-less tool call with empty arguments",
      violation: "tool-call-index-missing-fragmented",
      fragments: [{
        choices: [{
          index: 0,
          delta: {
            tool_calls: [{
              id: `${sentinel}_ID`,
              type: "function",
              function: { name: "read_file", arguments: "" },
            }],
          },
          finish_reason: "tool_calls",
        }],
      }],
    },
    {
      name: "multiple index-less tool calls",
      violation: "tool-call-index-missing-multiple",
      fragments: [{
        choices: [{
          index: 0,
          delta: { tool_calls: [fullCall(), fullCall()] },
          finish_reason: "tool_calls",
        }],
      }],
    },
    {
      name: "mixed indexed and index-less tool calls",
      violation: "tool-call-index-mixed",
      fragments: [
        {
          choices: [{
            index: 0,
            delta: { tool_calls: [{ index: 0, ...fullCall() }] },
            finish_reason: null,
          }],
        },
        {
          choices: [{
            index: 0,
            delta: { tool_calls: [fullCall()] },
            finish_reason: "tool_calls",
          }],
        },
      ],
    },
    {
      name: "invalid indexed tool call",
      violation: "tool-call-index-invalid",
      fragments: [{
        choices: [{
          index: 0,
          delta: { tool_calls: [{ index: "zero", ...fullCall() }] },
          finish_reason: "tool_calls",
        }],
      }],
    },
    {
      name: "tool call with non-tool-call finish reason",
      violation: "finish-tool-call-mismatch",
      fragments: [{
        choices: [{
          index: 0,
          delta: { tool_calls: [fullCall()] },
          finish_reason: "stop",
        }],
      }],
    },
    {
      name: "tool-call finish reason without tool calls",
      violation: "finish-tool-call-mismatch",
      fragments: [{
        choices: [{
          index: 0,
          delta: {},
          finish_reason: "tool_calls",
        }],
      }],
    },
    ...[null, "zero", -1, 1].map((index) => ({
      name: `invalid choice index ${String(index)}`,
      violation: "choice-index-invalid",
      fragments: [{
        choices: [{
          index,
          delta: { content: sentinel },
          finish_reason: "stop",
        }],
      }],
    })),
    {
      name: "ambiguous multi-choice omission",
      violation: "choice-index-missing-multiple",
      fragments: [{
        choices: [
          { delta: { content: sentinel }, finish_reason: "stop" },
          { delta: { content: sentinel }, finish_reason: "stop" },
        ],
      }],
    },
  ];

  for (const streamCase of cases) {
    const metrics = new RuntimeMetrics();
    let executions = 0;
    await assert.rejects(
      runAgent({
        client: clientFrom([completionStream(null, { fragments: streamCase.fragments })]),
        config: baseConfig,
        methodologies: [],
        prompt: "p",
        sandbox: {
          ...sandbox,
          readFile() {
            executions++;
            return "{}";
          },
        },
        schema,
        metrics,
      }),
      (error) => error.reason === "provider stream was malformed" && error.retryable &&
        error.streamStructuralViolation === streamCase.violation,
      streamCase.name,
    );
    assert.equal(executions, 0, streamCase.name);
    const diagnostics = metrics.snapshot();
    assert.equal(diagnostics.streamStructuralViolation, streamCase.violation, streamCase.name);
    assert.match(diagnostics.streamStructuralViolation, /^[a-z-]+$/, streamCase.name);
    assert.doesNotMatch(JSON.stringify(diagnostics), new RegExp(sentinel), streamCase.name);
  }
});

test("runtime rejects every non-no-op post-finish tail without executing tools", async () => {
  const sentinel = "POST_FINISH_STREAM_SECRET_SENTINEL";
  const toolCall = {
    index: 0,
    id: `${sentinel}_ID`,
    type: "function",
    function: { name: "read_file", arguments: `{"path":"${sentinel}_ARGUMENTS"}` },
  };
  const cases = [
    {
      name: "multiple choices",
      shape: "choice-count",
      choices: [{ index: 0, delta: {} }, { index: 0, delta: {} }],
    },
    { name: "choice object", shape: "choice-object", choices: [null] },
    ...[null, "zero", -1, 1].map((index) => ({
      name: `bad index ${String(index)}`,
      shape: "choice-index",
      choices: [{ index, delta: {} }],
    })),
    {
      name: "own undefined index",
      shape: "choice-index",
      choices: [{ index: undefined, delta: {} }],
    },
    {
      name: "inherited index",
      shape: "choice-index",
      choices: [Object.assign(Object.create({ index: 0 }), { delta: {} })],
    },
    {
      name: "ambiguous omitted index",
      shape: "choice-count",
      choices: [{ delta: {} }, { delta: {} }],
    },
    {
      name: "distinct finish reason",
      shape: "finish-mismatch",
      choices: [{ index: 0, delta: {}, finish_reason: "length" }],
    },
    {
      name: "own undefined finish reason",
      shape: "finish-mismatch",
      choices: [{ index: 0, delta: {}, finish_reason: undefined }],
    },
    {
      name: "inherited finish reason",
      shape: "finish-mismatch",
      choices: [Object.assign(Object.create({ finish_reason: "tool_calls" }), {
        index: 0, delta: {},
      })],
    },
    {
      name: "invalid delta",
      shape: "delta-invalid",
      choices: [{ index: 0, delta: null }],
    },
    {
      name: "array delta",
      shape: "delta-invalid",
      choices: [{ index: 0, delta: [] }],
    },
    {
      name: "inherited delta",
      shape: "delta-invalid",
      choices: [Object.assign(Object.create({ delta: {} }), { index: 0 })],
    },
    {
      name: "custom-prototype delta",
      shape: "delta-invalid",
      choices: [{ index: 0, delta: Object.create({}) }],
    },
    {
      name: "late content",
      shape: "delta-content",
      choices: [{ index: 0, delta: { content: sentinel } }],
    },
    {
      name: "empty content key",
      shape: "delta-content-empty",
      choices: [{ index: 0, delta: { content: "" } }],
    },
    {
      name: "null content key",
      shape: "delta-content-empty",
      choices: [{ index: 0, delta: { content: null } }],
    },
    {
      name: "empty content and reasoning keys",
      shape: "delta-content-empty",
      choices: [{ index: 0, delta: { content: "", reasoning_content: null } }],
    },
    {
      name: "empty content beside late reasoning",
      shape: "delta-reasoning",
      choices: [{ index: 0, delta: { content: "", reasoning_content: sentinel } }],
    },
    {
      name: "empty content beside tool calls",
      shape: "delta-tool-calls",
      choices: [{ index: 0, delta: { content: null, tool_calls: [] } }],
    },
    {
      name: "empty content beside an extension",
      shape: "delta-extension",
      choices: [{ index: 0, delta: { content: "", refusal: null } }],
    },
    {
      name: "late reasoning",
      shape: "delta-reasoning",
      choices: [{ index: 0, delta: { reasoning_content: sentinel } }],
    },
    {
      name: "empty reasoning",
      shape: "delta-reasoning-empty",
      choices: [{ index: 0, delta: { reasoning_content: "" } }],
    },
    {
      name: "null reasoning",
      shape: "delta-reasoning-empty",
      choices: [{ index: 0, delta: { reasoning_content: null } }],
    },
    {
      name: "late tool calls",
      shape: "delta-tool-calls",
      choices: [{ index: 0, delta: { tool_calls: [toolCall] } }],
    },
    {
      name: "empty tool calls",
      shape: "delta-tool-calls",
      choices: [{ index: 0, delta: { tool_calls: [] } }],
    },
    ...["role", "function_call", "refusal", "audio", "extension"].flatMap((key) => [
      {
        name: key,
        shape: "delta-extension",
        choices: [{ index: 0, delta: { [key]: sentinel } }],
      },
      {
        name: `empty ${key}`,
        shape: "delta-extension",
        choices: [{ index: 0, delta: { [key]: "" } }],
      },
    ]),
  ];

  for (const streamCase of cases) {
    const metrics = new RuntimeMetrics();
    let executions = 0;
    await assert.rejects(
      runAgent({
        client: clientFrom([completionStream(null, {
          fragments: [
            {
              choices: [{
                index: 0,
                delta: { tool_calls: [toolCall] },
                finish_reason: "tool_calls",
              }],
            },
            { choices: streamCase.choices },
          ],
        })]),
        config: baseConfig,
        methodologies: [],
        prompt: "p",
        sandbox: {
          ...sandbox,
          readFile() {
            executions++;
            return "{}";
          },
        },
        schema,
        metrics,
      }),
      (error) => error.reason === "provider stream was malformed" && error.retryable &&
        error.streamStructuralViolation === "post-finish" &&
        error.postFinishShape === streamCase.shape,
      streamCase.name,
    );
    assert.equal(executions, 0, streamCase.name);
    const diagnostics = metrics.snapshot();
    assert.equal(diagnostics.streamStructuralViolation, "post-finish", streamCase.name);
    assert.equal(diagnostics.postFinishShape, streamCase.shape, streamCase.name);
    assert.equal(diagnostics.ignoredPostFinishEmptyDeltaChoices, 0, streamCase.name);
    assert.equal(diagnostics.ignoredRepeatedTerminalChoices, 0, streamCase.name);
    assert.match(diagnostics.postFinishShape, /^[a-z-]+$/, streamCase.name);
    assert.doesNotMatch(JSON.stringify(diagnostics), new RegExp(sentinel), streamCase.name);
  }
});

test("runtime executes no tool when any streamed envelope is incomplete", async () => {
  let executions = 0;
  const fragments = [{
    choices: [{
      index: 0,
      delta: {
        tool_calls: [
          { index: 0, id: "valid", type: "function", function: { name: "read_file", arguments: "{}" } },
          { index: 1, type: "function", function: { name: "read_file", arguments: "{}" } },
        ],
      },
      finish_reason: "tool_calls",
    }],
  }];
  await assert.rejects(
    runAgent({
      client: clientFrom([completionStream(null, { fragments })]),
      config: baseConfig,
      methodologies: [],
      prompt: "p",
      sandbox: {
        ...sandbox,
        readFile() {
          executions++;
          return "{}";
        },
      },
      schema,
    }),
    (error) => error.reason === "provider returned a malformed tool call",
  );
  assert.equal(executions, 0);
});

test("stream retry discards partial fragments without spending another logical turn", async () => {
  let executions = 0;
  const interrupted = {
    controller: new AbortController(),
    async *[Symbol.asyncIterator]() {
      yield {
        choices: [{
          index: 0,
          delta: {
            tool_calls: [{
              index: 0,
              id: "partial",
              type: "function",
              function: { name: "read_file", arguments: '{"path":"' },
            }],
          },
          finish_reason: null,
        }],
      };
      throw Object.assign(new TypeError("terminated"), { cause: { code: "UND_ERR_SOCKET" } });
    },
  };
  const result = await runAgent({
    client: clientFrom([interrupted, message('{"answer":"retry"}')]),
    config: { ...baseConfig, max_request_retries: 1 },
    methodologies: [],
    prompt: "p",
    sandbox: {
      ...sandbox,
      readFile() {
        executions++;
        return "{}";
      },
    },
    schema,
    retrySleep: async () => {},
  });

  assert.equal(result.output, '{"answer":"retry"}');
  assert.equal(result.turnCount, 1);
  assert.equal(executions, 0);
});

test("malformed provider streams retry the unchanged call without executing tools", async () => {
  let executions = 0;
  const requests = [];
  const delays = [];
  const toolCall = {
    index: 0,
    id: "malformed",
    type: "function",
    function: { name: "read_file", arguments: '{"path":"a"}' },
  };
  const result = await runAgent({
    client: clientFrom([
      completionStream(null, {
        fragments: [
          { choices: [{ index: 0, delta: { tool_calls: [toolCall] }, finish_reason: "tool_calls" }] },
          { choices: [{ index: 0, delta: { content: "" } }] },
        ],
      }),
      new SyntaxError("RAW_SSE_SECRET_SENTINEL"),
      message('{"answer":"retry"}'),
    ], requests),
    config: { ...baseConfig, max_request_retries: 2 },
    methodologies: [],
    prompt: "p",
    sandbox: {
      ...sandbox,
      readFile() {
        executions++;
        return "{}";
      },
    },
    schema,
    retrySleep: async (milliseconds) => { delays.push(milliseconds); },
  });

  assert.equal(result.output, '{"answer":"retry"}');
  assert.equal(result.turnCount, 1);
  assert.equal(result.toolCallCount, 0);
  assert.equal(executions, 0);
  assert.equal(requests.length, 3);
  assert.deepEqual(requests[1], requests[0]);
  assert.deepEqual(requests[2], requests[0]);
  assert.equal(delays.length, 2);
});

test("malformed provider streams fail retryably once the request-retry budget is spent", async () => {
  const requests = [];
  const delays = [];
  const malformed = () => completionStream(null, {
    fragments: [
      { choices: [{ index: 0, delta: { content: '{"answer":"done"}' }, finish_reason: "stop" }] },
      { choices: [{ index: 0, delta: { content: "late" } }] },
    ],
  });
  await assert.rejects(
    runAgent({
      client: clientFrom([malformed(), malformed(), malformed()], requests),
      config: { ...baseConfig, max_request_retries: 2 },
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
      retrySleep: async (milliseconds) => { delays.push(milliseconds); },
    }),
    (error) => error instanceof AgentFailure &&
      error.reason === "provider stream was malformed" &&
      error.category === "provider-response" && error.retryable &&
      error.streamStructuralViolation === "post-finish" &&
      error.postFinishShape === "delta-content" && error.turnCount === 1,
  );
  assert.equal(requests.length, 3);
  assert.equal(delays.length, 2);
});

test("real SDK retries malformed streams and records each attempt's violation", async () => {
  const metrics = new RuntimeMetrics();
  const sentinel = "MALFORMED_STREAM_SECRET_SENTINEL";
  const sse = (body) => new Response(body, {
    status: 200,
    headers: { "content-type": "text/event-stream" },
  });
  const chunk = (choices) => `data: ${JSON.stringify({
    id: "completion", object: "chat.completion.chunk", created: 0, model: "test", choices,
  })}\n\n`;
  let calls = 0;
  const client = createProviderClient(OpenAI, {
    apiKey: "test-key",
    baseURL: "https://provider.example/v1",
  }, metrics, async () => {
    calls++;
    if (calls === 1) {
      return sse(`${chunk([{
        index: 0, delta: { content: '{"answer":"done"}' }, finish_reason: "stop",
      }])}${chunk([{ index: 0, delta: { content: "" } }])}data: [DONE]\n\n`);
    }
    if (calls === 2) return sse(`data: {"${sentinel}\n\n`);
    return sseResponse('{"answer":"done"}');
  });

  const result = await runAgent({
    client,
    config: { ...baseConfig, max_request_retries: 4 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
    metrics,
    retrySleep: async () => {},
  });

  assert.equal(result.output, '{"answer":"done"}');
  assert.equal(calls, 3);
  const diagnostics = metrics.snapshot();
  assert.equal(diagnostics.requestRetryCount, 2);
  assert.equal(diagnostics.streamStructuralViolation, "post-finish");
  assert.equal(diagnostics.postFinishShape, "delta-content-empty");
  assert.deepEqual(
    diagnostics.providerAttempts.map(({ streamStructuralViolation, postFinishShape }) => ({
      streamStructuralViolation, postFinishShape,
    })),
    [
      { streamStructuralViolation: "post-finish", postFinishShape: "delta-content-empty" },
      { streamStructuralViolation: "sse-json-invalid", postFinishShape: undefined },
      { streamStructuralViolation: undefined, postFinishShape: undefined },
    ],
  );
  assert.doesNotMatch(JSON.stringify(diagnostics), new RegExp(sentinel));
});

test("runtime accepts length-finished text and marks missing completion metadata retryable", async () => {
  const text = completionStream(null, {
    fragments: [{ choices: [{ index: 0, delta: { content: '{"answer":"done"}' }, finish_reason: "length" }] }],
  });
  const result = await runAgent({
    client: clientFrom([text]),
    config: { ...baseConfig, max_tool_calls: 0 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
  });
  assert.equal(result.output, '{"answer":"done"}');

  const incomplete = completionStream(null, {
    fragments: [{
      choices: [{
        index: 0,
        delta: { content: '{"answer":"truncated"}' },
        finish_reason: null,
      }],
    }],
  });
  await assert.rejects(
    runAgent({
      client: clientFrom([incomplete]),
      config: { ...baseConfig, max_tool_calls: 0 },
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
    }),
    (error) => error.reason === "provider stream ended without completion metadata" &&
      error.category === "provider-connection" && error.retryable,
  );
});

test("stage deadline aborts a stalled stream", async () => {
  const stalled = {
    controller: new AbortController(),
    [Symbol.asyncIterator]() {
      return {
        next: () => new Promise(() => {}),
        return: async () => ({ done: true }),
      };
    },
  };
  await assert.rejects(
    runAgent({
      client: clientFrom([stalled]),
      config: { ...baseConfig, stage_timeout_ms: 20 },
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
    }),
    (error) => error.reason === "stage deadline exceeded" && error.category === "limit",
  );
  assert.equal(stalled.controller.signal.aborted, true);
});

test("stage deadline rejects a synchronous validator that returns too late", async () => {
  await assert.rejects(
    runAgent({
      client: clientFrom([message('{"answer":"done"}')]),
      config: { ...baseConfig, max_tool_calls: 0, stage_timeout_ms: 10 },
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
      validator() {
        const deadline = performance.now() + 40;
        while (performance.now() < deadline) {
          // Deliberately block the event loop so only the monotonic post-check can catch the overrun.
        }
        return { ok: true };
      },
    }),
    (error) => error.reason === "stage deadline exceeded" && error.category === "limit",
  );
});

test("runtime reports malformed arguments and unknown tools without executing them", async () => {
  const requests = [];
  let executions = 0;
  const guardedSandbox = {
    readFile() { executions++; },
    listFiles() { executions++; },
    searchText() { executions++; },
  };
  const client = clientFrom([
    message(null, [
      call("bad-json", "read_file", "{"),
      call("unknown", "run_shell", {}),
    ]),
    message('{"answer":"safe"}'),
  ], requests);
  const result = await runAgent({
    client, config: baseConfig, methodologies: [], prompt: "p", sandbox: guardedSandbox, schema,
  });
  assert.equal(result.toolCallCount, 2);
  assert.equal(executions, 0);
  assert.match(requests[1].messages.at(-2).content, /malformed tool arguments/);
  assert.match(requests[1].messages.at(-1).content, /unknown tool/);
});

test("runtime rejects malformed tool call envelopes", async () => {
  const client = clientFrom([message(null, [{ id: "", type: "function", function: {} }])]);
  await assert.rejects(
    runAgent({ client, config: baseConfig, methodologies: [], prompt: "p", sandbox, schema }),
    (error) => error instanceof AgentFailure && error.reason === "provider returned a malformed tool call",
  );
});

test("runtime applies its model-message-content limit while assembling streamed text", async () => {
  const requests = [];
  await assert.rejects(
    runAgent({
      client: clientFrom([message("x".repeat(1024 * 1024 + 1))], requests),
      config: baseConfig, methodologies: [], prompt: "p", sandbox, schema,
    }),
    (error) => error.reason === "model message content exceeded byte limit" && error.turnCount === 1,
  );
  assert.equal(requests.length, 1);
});

test("output validation sanitizes unpaired UTF-16 surrogates and rejects key collisions", () => {
  const validate = compileOutputValidator(schema);
  for (const value of ["\uD800", "\uDC00"]) {
    assert.deepEqual(validate(JSON.stringify({ answer: value })), {
      ok: true,
      output: '{"answer":"�"}',
      value: { answer: "�" },
    });
  }
  const dynamic = compileOutputValidator({ type: "object" });
  assert.deepEqual(dynamic(JSON.stringify({ nested: { ["\uD800"]: "accepted" } })), {
    ok: true,
    output: '{"nested":{"�":"accepted"}}',
    value: { nested: { "�": "accepted" } },
  });
  const depth = 20_000;
  const deeplyNested = `${'{"nested":'.repeat(depth)}"\uD800"${"}".repeat(depth)}`;
  const deeplyNestedResult = dynamic(deeplyNested);
  assert.equal(deeplyNestedResult.ok, false);
  assert.equal(deeplyNestedResult.layer, "json");
  assert.equal(deeplyNestedResult.reason, "response nesting exceeded the serialization limit");
  assert.deepEqual(dynamic('{"\\ud800":"first","�":"second"}'), {
    ok: false,
    layer: "unicode",
    reason: "response property names collide after Unicode sanitization",
  });
  assert.equal(validate(JSON.stringify({ answer: "😀" })).ok, true);
});

test("output normalization is canonical, ordered, and rejects invalid trusted returns", async () => {
  const calls = [];
  const normalize = (value) => {
    calls.push("normalize");
    return { answer: value.answer.trim() };
  };
  const validate = compileOutputValidator(schema, normalize);
  assert.deepEqual(validate('{"answer":" done ","extra":"discarded"}'), {
    ok: true, output: '{"answer":"done"}', value: { answer: "done" },
  });
  assert.deepEqual(calls, ["normalize"]);

  let invoked = false;
  const never = compileOutputValidator(schema, () => { invoked = true; return {}; });
  assert.equal(never("{").layer, "json");
  assert.equal(never('{"\\ud800":"first","�":"second"}').layer, "unicode");
  assert.equal(invoked, false);

  for (const value of [
    Promise.resolve({}), { then() {} }, (() => { const cycle = {}; cycle.self = cycle; return cycle; })(),
    NaN, 1n, undefined, () => {}, Symbol("value"), new Date(),
  ]) {
    assert.throws(
      () => compileOutputValidator(schema, () => value)('{"answer":"safe"}'),
      (error) => error.category === "normalizer-error" &&
        error.reason === "normalizer returned an invalid JSON value",
    );
  }
  let thenGetterCalls = 0;
  const accessor = {};
  Object.defineProperty(accessor, "then", {
    enumerable: true,
    get() {
      thenGetterCalls++;
      return () => {};
    },
  });
  assert.throws(
    () => compileOutputValidator(schema, () => accessor)('{"answer":"safe"}'),
    (error) => error.category === "normalizer-error",
  );
  assert.equal(thenGetterCalls, 0);
  const symbolKeyed = { answer: "safe", [Symbol("hidden")]: "value" };
  assert.throws(
    () => compileOutputValidator(schema, () => symbolKeyed)('{"answer":"safe"}'),
    (error) => error.category === "normalizer-error",
  );
  assert.throws(
    () => compileOutputValidator(schema, () => ({ "\ud800": "first", "�": "second" }))(
      '{"answer":"safe"}',
    ),
    (error) => error.category === "normalizer-error",
  );

  const semanticCalls = [];
  const result = await runAgent({
    client: clientFrom([message('{"answer":" normalized "}')]),
    config: { ...baseConfig, max_tool_calls: 0 },
    methodologies: [], normalizer: normalize, prompt: "p", sandbox, schema,
    validator: async (candidate) => {
      semanticCalls.push(candidate.answer);
      return { ok: candidate.answer === "normalized" };
    },
  });
  assert.equal(result.output, '{"answer":"normalized"}');
  assert.deepEqual(semanticCalls, ["normalized"]);

  await assert.rejects(
    runAgent({
      client: clientFrom([message('{"answer":"safe"}')]),
      config: { ...baseConfig, max_tool_calls: 0 },
      methodologies: [], normalizer: () => { throw new Error("MODEL_SECRET_SENTINEL"); },
      prompt: "p", sandbox, schema,
    }),
    (error) => error.category === "normalizer-error" &&
      error.reason === "normalizer returned an invalid JSON value" &&
      error.outputRepairCount === 0,
  );
});

test("schema repair diagnostics are bounded, actionable, and provider-data-free", () => {
  const cases = [
    [{ type: "object", required: ["safe"], properties: { safe: { type: "string" } } }, {}, "required safe"],
    [{ type: "string", maxLength: 1 }, "xx", "at most 1 characters"],
    [{ type: "string", minLength: 2 }, "x", "at least 2 characters"],
    [{ type: "array", maxItems: 1 }, [1, 2], "at most 1 items"],
    [{ type: "array", minItems: 2 }, [1], "at least 2 items"],
    [{ type: "number", maximum: 1 }, 2, "at most 1"],
    [{ type: "number", minimum: 1 }, 0, "at least 1"],
    [{ type: "string" }, 1, "wrong type"],
    [{ enum: ["safe"] }, "MODEL_RESPONSE_SECRET_SENTINEL", "unsupported value"],
    [{ type: "string", pattern: "^safe$" }, "MODEL_RESPONSE_SECRET_SENTINEL", "invalid format"],
    [{ type: "object", additionalProperties: false }, { MODEL_RESPONSE_SECRET_SENTINEL: true }, "unexpected property"],
    [{ type: "array", uniqueItems: true }, ["safe", "safe"], "duplicate items"],
  ];
  for (const [schema, value, detail] of cases) {
    const result = compileOutputValidator(schema)(JSON.stringify(value));
    assert.equal(result.ok, false);
    assert.equal(result.layer, "schema");
    assert.match(result.reason, new RegExp(detail));
    assert.doesNotMatch(result.reason, /MODEL_RESPONSE_SECRET_SENTINEL/);
    assert.ok(Buffer.byteLength(result.reason, "utf8") <= 512);
  }
  const nested = compileOutputValidator({
    type: "object",
    properties: { items: { type: "array", items: { type: "object", properties: { safe: { type: "string" } } } } },
  })('{"items":[{"safe":1}]}');
  assert.match(nested.reason, /#\/items\/0\/safe: has wrong type/);
});

test("runtime enforces aggregate tool-call and turn bounds", async () => {
  const calls = [call("one", "read_file", { path: "x" }), call("two", "read_file", { path: "x" })];
  await assert.rejects(
    runAgent({
      client: clientFrom([message(null, calls)]),
      config: { ...baseConfig, max_tool_calls: 1 },
      methodologies: [], prompt: "p", sandbox, schema,
    }),
    (error) => error.reason === "maximum tool call count exceeded" && error.toolCallCount === 0,
  );

  await assert.rejects(
    runAgent({
      client: clientFrom([message("not-json")]),
      config: { ...baseConfig, max_turns: 1 },
      methodologies: [], prompt: "p", sandbox, schema,
    }),
    (error) => error.reason === "maximum turn count exceeded" &&
      error.turnCount === 1 && error.toolCallCount === 0,
  );
});

test("runtime reserves tool-free finalization and repair turns", async () => {
  const requests = [];
  const result = await runAgent({
    client: clientFrom([
      message(null, [call("one", "read_file", { path: "x" })]),
      message(null, [call("two", "read_file", { path: "x" })]),
      message("not-json"),
      message('{"answer":"repaired"}'),
    ], requests),
    // Without a validator a repair cannot use tools, so one repair call plus finalization
    // reserves two, leaving two for investigation.
    config: baseConfig,
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
  });

  assert.equal(result.output, '{"answer":"repaired"}');
  assert.equal(result.turnCount, 4);
  assert.equal(result.toolCallCount, 2);
  assert.deepEqual(requests.map((request) => request.tools !== undefined), [true, true, false, false]);
  assert.deepEqual(requests[2].response_format, { type: "json_object" });
  assert.deepEqual(requests[3].response_format, { type: "json_object" });
  assert.match(requests[2].messages.at(-1).content, /Investigation is complete/);
});

test("an undersized turn ceiling reports the validation error rather than the limit", async () => {
  // The clamp can leave a repair without room for both an evidence call and the answer that
  // follows it. Asking for the correction directly keeps the semantic reason on the failure.
  const respond = (request) => request.tools !== undefined
    ? message(null, [call("t", "read_file", { path: "x" })])
    : message('{"answer":"bad"}');

  await assert.rejects(
    runAgent({
      client: clientFrom(Array(8).fill(respond)),
      config: { ...baseConfig, max_turns: 4, max_tool_calls: 8, max_output_repair_attempts: 2 },
      methodologies: [], prompt: "p", sandbox, schema,
      validator: async () => ({ ok: false, reason: "citation requires source verification" }),
    }),
    (error) => {
      assert.match(error.reason, /citation requires source verification/);
      assert.equal(error.category, "output-invalid");
      return true;
    },
  );
});

test("a saturated investigation still leaves every repair attempt reachable", async () => {
  // Each semantic rejection buys one evidence call before the next answer, so both repair
  // attempts exercise the two-provider-call maximum. Tools are always taken, leaving the
  // reservation alone to decide when investigation ends.
  const answers = ['{"answer":"bad"}', '{"answer":"bad2"}', '{"answer":"good"}'];
  const activities = [];
  const respond = (request) => {
    const toolsOffered = request.tools !== undefined;
    activities.push(toolsOffered ? "tools" : "answer");
    return toolsOffered
      ? message(null, [call(`t${activities.length}`, "read_file", { path: "x" })])
      : message(answers.shift());
  };

  const result = await runAgent({
    client: clientFrom(Array(12).fill(respond)),
    config: { ...baseConfig, max_turns: 8, max_tool_calls: 20, max_output_repair_attempts: 2 },
    methodologies: [], prompt: "p", sandbox, schema,
    validator: async (candidate) => candidate.answer === "good"
      ? { ok: true }
      : { ok: false, reason: "citation requires source verification" },
  });

  assert.equal(result.output, '{"answer":"good"}');
  assert.equal(result.outputRepairCount, 2);
  assert.equal(result.turnCount, 8);
  // Three investigation turns, a tool-free finalization, then two tool-assisted repairs.
  assert.deepEqual(activities, [
    "tools", "tools", "tools", "answer", "tools", "answer", "tools", "answer",
  ]);
});

test("runtime stops advertising tools after exhausting the configured budget", async () => {
  const requests = [];
  await runAgent({
    client: clientFrom([
      message(null, [call("one", "read_file", { path: "x" })]),
      message('{"answer":"done"}'),
    ], requests),
    config: { ...baseConfig, max_tool_calls: 1 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
  });
  assert.deepEqual(requests[0].tools, TOOLS);
  assert.equal(requests[1].tools, undefined);
});

test("runtime allows exactly one tools-disabled repair for JSON or schema failure", async () => {
  const requests = [];
  const client = clientFrom([
    message('{"wrong":true}'),
    message('{"answer":"repaired"}'),
  ], requests);
  const result = await runAgent({
    client, config: baseConfig, methodologies: [], prompt: "p", sandbox, schema,
  });

  assert.equal(result.output, '{"answer":"repaired"}');
  assert.equal(result.turnCount, 2);
  assert.equal(requests[1].tools, undefined);
  assert.equal(requests[1].tool_choice, undefined);
  assert.equal(requests[1].parallel_tool_calls, undefined);
  assert.deepEqual(requests[1].response_format, { type: "json_object" });
  assert.match(requests[1].messages.at(-1).content, /Do not call tools/);
  assert.match(requests[1].messages.at(-1).content, /Correct every reported validation error/);
  assert.match(requests[1].messages.at(-1).content, /no Markdown fences/);

  await assert.rejects(
    runAgent({
      client: clientFrom([message('{"wrong":true}'), message("not-json")]),
      config: baseConfig, methodologies: [], prompt: "p", sandbox, schema,
    }),
    (error) => error.reason ===
      "output remained invalid after the repair limit: json: response was not valid JSON" &&
      error.category === "output-invalid" &&
      error.turnCount === 2 && error.outputRepairCount === 1,
  );
});

test("exhausting repairs reports the layer and reason that ended the stage", async () => {
  const metrics = new RuntimeMetrics();
  await assert.rejects(
    runAgent({
      client: clientFrom([
        message("not-json"),
        message('{"wrong":true}'),
        message('{"wrong":true}'),
      ]),
      config: { ...baseConfig, max_output_repair_attempts: 2 },
      methodologies: [], prompt: "p", sandbox, schema, metrics,
    }),
    (error) => error.reason ===
      "output remained invalid after the repair limit: schema: response did not match the schema: #: required answer; #: has an unexpected property" &&
      error.category === "output-invalid" && !error.retryable && error.outputRepairCount === 2,
  );
  assert.deepEqual(metrics.snapshot().outputRejections, [
    { attempt: 1, activity: "investigating", layer: "json", reason: "response was not valid JSON" },
    {
      attempt: 2,
      activity: "repairing",
      layer: "schema",
      reason: "response did not match the schema: #: required answer; #: has an unexpected property",
    },
    {
      attempt: 3,
      activity: "repairing",
      layer: "schema",
      reason: "response did not match the schema: #: required answer; #: has an unexpected property",
    },
  ]);
});

test("a semantic rejection is reported as its own validation layer", async () => {
  const metrics = new RuntimeMetrics();
  await assert.rejects(
    runAgent({
      client: clientFrom([message('{"answer":"a"}'), message('{"answer":"b"}')]),
      config: { ...baseConfig, max_tool_calls: 0, max_output_repair_attempts: 1 },
      methodologies: [], prompt: "p", sandbox, schema, metrics,
      validator: async () => ({ ok: false, reason: "citation requires source verification" }),
    }),
    (error) => error.reason ===
      "output remained invalid after the repair limit: semantic: citation requires source verification",
  );
  assert.deepEqual(
    metrics.snapshot().outputRejections.map((rejection) => rejection.layer),
    ["semantic", "semantic"],
  );
});

test("validator detail and guidance reach repair while only detail reaches diagnostics", async () => {
  const metrics = new RuntimeMetrics();
  const requests = [];
  await assert.rejects(
    runAgent({
      client: clientFrom([message('{"answer":"a"}'), message('{"answer":"b"}')], requests),
      config: { ...baseConfig, max_tool_calls: 0, max_output_repair_attempts: 1 },
      methodologies: [], prompt: "p", sandbox, schema, metrics,
      validator: async () => ({
        ok: false,
        reason: "2 candidates lack a disposition",
        detail: "2 of 2 candidates have no valid disposition, at aggregate findings skeptical 0, 1",
        guidance: "candidates: (skeptical, first-id); (skeptical, second-id)",
      }),
    }),
    (error) => error.reason ===
      "output remained invalid after the repair limit: semantic: 2 candidates lack a disposition",
  );
  const repair = requests[1].messages.at(-1).content;
  assert.match(repair, /at aggregate findings skeptical 0, 1/);
  assert.match(repair, /\(skeptical, first-id\); \(skeptical, second-id\)/);
  assert.doesNotMatch(repair, /2 candidates lack a disposition/);
  const [first] = metrics.snapshot().outputRejections;
  assert.deepEqual(first, {
    attempt: 1,
    activity: "finalizing",
    layer: "semantic",
    reason: "2 candidates lack a disposition",
    detail: "2 of 2 candidates have no valid disposition, at aggregate findings skeptical 0, 1",
  });
  assert.doesNotMatch(JSON.stringify(metrics.snapshot()), /first-id/);
});

test("clean runs report no rejection diagnostics", async () => {
  const metrics = new RuntimeMetrics();
  await runAgent({
    client: clientFrom([message('{"answer":"done"}')]),
    config: { ...baseConfig, max_tool_calls: 0 },
    methodologies: [], prompt: "p", sandbox, schema, metrics,
  });
  assert.equal(Object.hasOwn(metrics.snapshot(), "outputRejections"), false);
});

test("rejection diagnostics are bounded and stripped of unexpected characters", async () => {
  const metrics = new RuntimeMetrics();
  const responses = Array.from({ length: 12 }, () => message("not-json"));
  await assert.rejects(
    runAgent({
      client: clientFrom(responses),
      config: { ...baseConfig, max_turns: 12, max_tool_calls: 0, max_output_repair_attempts: 10 },
      methodologies: [], prompt: "p", sandbox, schema, metrics,
    }),
    (error) => error.category === "output-invalid",
  );
  assert.equal(metrics.snapshot().outputRejections.length, 8);

  assert.equal(sanitizeReason("keeps a-z 0.9, (x): #/y_z"), "keeps a-z 0.9, (x): #/y_z");
  assert.equal(sanitizeReason("drops\nnewlines\tand \"quotes\" <tags>"), "drops newlines and quotes tags");
  assert.equal(sanitizeReason("x".repeat(400)).length, 240);
});

test("runtime rejects fenced repair output despite requesting JSON mode", async () => {
  const requests = [];
  await assert.rejects(
    runAgent({
      client: clientFrom([
        message('{"wrong":true}'),
        message('```json\n{"answer":"wrapped"}\n```'),
      ], requests),
      config: baseConfig,
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
    }),
    (error) => error.reason ===
      "output remained invalid after the repair limit: json: response was not valid JSON" &&
      error.category === "output-invalid" && error.turnCount === 2,
  );
  assert.deepEqual(requests[1].response_format, { type: "json_object" });
});

test("a tool-assisted repair still answers under the configured response format", async () => {
  const requests = [];
  const observed = [];
  const validator = async (candidate, context) => {
    observed.push({ candidate, ...context });
    if (candidate.answer === "missing citation") {
      return { ok: false, reason: "citation requires source verification" };
    }
    return { ok: true };
  };
  const result = await runAgent({
    client: clientFrom([
      message('{"answer":"missing citation"}'),
      message(null, [call("citation", "read_file", { path: "evidence.txt" })]),
      message('{"answer":"cited"}'),
    ], requests),
    config: { ...baseConfig, max_turns: 4, max_output_repair_attempts: 1 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
    validator,
  });

  assert.equal(result.output, '{"answer":"cited"}');
  assert.equal(result.outputRepairCount, 1);
  assert.deepEqual(observed, [
    {
      candidate: { answer: "missing citation" },
      previousCandidate: null,
      candidates: [],
      repairAttempt: 0,
    },
    {
      candidate: { answer: "cited" },
      previousCandidate: { answer: "missing citation" },
      candidates: [{ answer: "missing citation" }],
      repairAttempt: 1,
    },
  ]);
  // Evidence lookup is offered once; once the tool results are in, the corrected value is requested
  // without tools so it is produced under the response format instead of unconstrained.
  assert.deepEqual(requests.map((request) => request.tools !== undefined), [true, true, false]);
  assert.equal(requests[1].response_format, undefined);
  assert.deepEqual(requests[2].response_format, { type: "json_object" });
  assert.match(requests[1].messages.at(-1).content, /not to begin a new investigation/);
  assert.equal(requests[2].messages.at(-1).role, "tool");
});

test("a repair that answers immediately is never offered a second tool turn", async () => {
  const requests = [];
  await assert.rejects(
    runAgent({
      client: clientFrom([
        message('{"answer":"rejected"}'),
        message(null, [call("first", "read_file", { path: "evidence.txt" })]),
        message(null, [call("second", "read_file", { path: "evidence.txt" })]),
      ], requests),
      config: { ...baseConfig, max_turns: 4, max_output_repair_attempts: 1 },
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
      validator: async () => ({ ok: false, reason: "citation requires source verification" }),
    }),
    (error) => error.reason === "repair response attempted a tool call" &&
      error.category === "provider-response",
  );
  assert.deepEqual(requests.map((request) => request.tools !== undefined), [true, true, false]);
});

test("strict output constrains the answer a repair returns after evidence", async () => {
  const requests = [];
  const strictFormat = {
    type: "json_schema",
    json_schema: { name: "structured_output", strict: true, schema },
  };
  const result = await runAgent({
    client: clientFrom([
      message('{"answer":"missing citation"}'),
      message(null, [call("citation", "read_file", { path: "evidence.txt" })]),
      message('{"answer":"cited"}'),
    ], requests),
    config: {
      ...baseConfig, max_turns: 4, max_output_repair_attempts: 1, output_format: "json_schema",
    },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
    validator: async (candidate) => candidate.answer === "missing citation"
      ? { ok: false, reason: "citation requires source verification" }
      : { ok: true },
  });

  assert.equal(result.output, '{"answer":"cited"}');
  // Constraining the turn that answers has to come out of the configured repair budget, not out of a
  // correction the budget never accounted for.
  assert.equal(result.outputRepairCount, 1);
  assert.deepEqual(requests[2].response_format, strictFormat);
});

test("a repair that answers without evidence answers unconstrained but validated", async () => {
  const requests = [];
  const validated = [];
  const result = await runAgent({
    client: clientFrom([
      message('{"answer":"missing citation"}'),
      message('{"answer":"cited"}'),
    ], requests),
    config: {
      ...baseConfig, max_turns: 4, max_output_repair_attempts: 1, output_format: "json_schema",
    },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
    validator: async (candidate) => {
      validated.push(candidate);
      return candidate.answer === "missing citation"
        ? { ok: false, reason: "citation requires source verification" }
        : { ok: true };
    },
  });

  // Offering tools is what costs the request its response format, so a repair that answers before
  // looking anything up answers unconstrained. Nothing accepts it but the schema and the validator,
  // which are what decide every result, constrained or not.
  assert.equal(result.output, '{"answer":"cited"}');
  assert.equal(requests[1].tools !== undefined, true);
  assert.equal(requests[1].response_format, undefined);
  assert.deepEqual(validated, [{ answer: "missing citation" }, { answer: "cited" }]);
});

test("validator sees every parsed candidate with the earliest still first", async () => {
  const observed = [];
  const reviewSchema = {
    type: "object",
    additionalProperties: false,
    required: ["summary", "findings"],
    properties: {
      summary: { type: "string" },
      findings: { type: "array" },
    },
  };
  const result = await runAgent({
    client: clientFrom([
      message('{"summary":"original","findings":["preserve"],"unexpected":true}'),
      message('{"findings":[]}'),
      message('{"summary":"complete","findings":[]}'),
    ]),
    config: { ...baseConfig, max_turns: 4, max_tool_calls: 0, max_output_repair_attempts: 2 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema: reviewSchema,
    validator: async (candidate, context) => {
      observed.push({ candidate, ...context });
      return { ok: true };
    },
  });

  assert.equal(result.output, '{"summary":"complete","findings":[]}');
  assert.deepEqual(observed, [{
    candidate: { summary: "complete", findings: [] },
    previousCandidate: { summary: "original", findings: ["preserve"], unexpected: true },
    // A finding first added by an intermediate repair is only protectable if the validator is told
    // that repair happened, so the whole ordered history travels with the baseline.
    candidates: [
      { summary: "original", findings: ["preserve"], unexpected: true },
      { findings: [] },
    ],
    repairAttempt: 2,
  }]);
});

test("validator retains falsy parsed candidates as repair baselines", async () => {
  for (const [first, second, expected] of [
    ["0", "false", 0],
    ["false", "[]", false],
    ["null", "[]", null],
    ["[]", "false", []],
  ]) {
    const observed = [];
    await runAgent({
      client: clientFrom([
        message("not JSON"),
        message(first),
        message(second),
        message('{"answer":"complete"}'),
      ]),
      config: { ...baseConfig, max_turns: 5, max_tool_calls: 0, max_output_repair_attempts: 3 },
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
      validator: async (candidate, context) => {
        observed.push({ candidate, ...context });
        return { ok: true };
      },
    });
    assert.deepEqual(observed, [{
      candidate: { answer: "complete" },
      previousCandidate: expected,
      candidates: [expected, JSON.parse(second)],
      repairAttempt: 3,
    }]);
  }
});

test("validator execution failures are terminal and strict output is opt-in", async () => {
  const terminal = new Error("validation context is stale");
  terminal.code = "VALIDATOR_TERMINAL";
  await assert.rejects(
    runAgent({
      client: clientFrom([message('{"answer":"candidate"}')]),
      config: baseConfig,
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
      validator: async () => { throw Object.assign(terminal, {
        reason: "validation context is stale",
        category: "validator-terminal",
      }); },
    }),
    (error) => error.category === "validator-terminal" &&
      error.reason === "validation context is stale" && error.outputRepairCount === 0,
  );

  const requests = [];
  await runAgent({
    client: clientFrom([message('{"answer":"candidate"}')], requests),
    config: { ...baseConfig, max_tool_calls: 0, output_format: "json_schema" },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
  });
  assert.deepEqual(requests[0].response_format, {
    type: "json_schema",
    json_schema: { name: "structured_output", strict: true, schema },
  });
});

test("runtime repairs a final response with no text", async () => {
  const requests = [];
  const result = await runAgent({
    client: clientFrom([
      message(null),
      message('{"answer":"repaired"}'),
    ], requests),
    config: baseConfig,
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
  });

  assert.equal(result.output, '{"answer":"repaired"}');
  assert.equal(result.turnCount, 2);
  assert.equal(requests[1].tools, undefined);
  assert.match(requests[1].messages.at(-1).content, /response was empty/);
});

test("runtime rejects malformed content without attempting repair", async () => {
  const requests = [];
  await assert.rejects(
    runAgent({
      client: clientFrom([message({ text: '{"answer":"invalid"}' })], requests),
      config: baseConfig,
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
    }),
    (error) => error.reason === "provider stream was malformed",
  );
  assert.equal(requests.length, 1);
});

test("validation diagnostics do not expose model-provided property names", () => {
  const validateOutput = compileOutputValidator({
    type: "object",
    patternProperties: { "^.+$": { type: "string" } },
  });
  const candidate = validateOutput('{"MODEL_RESPONSE_SECRET_SENTINEL":42}');
  assert.equal(candidate.ok, false);
  assert.match(candidate.reason, /^response did not match the schema: #\//);
  assert.doesNotMatch(candidate.reason, /MODEL_RESPONSE_SECRET_SENTINEL/);
});

test("runtime accepts schema-valid output that exceeds removed profile byte budgets", async () => {
  const requests = [];
  const output = JSON.stringify({ answer: "x".repeat(2000) });
  const result = await runAgent({
    client: clientFrom([
      message(output),
    ], requests),
    config: baseConfig,
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
  });
  assert.equal(result.output, output);
  assert.equal(requests.length, 1);
});

test("repair rejects provider tool calls and does not execute them", async () => {
  let executed = false;
  await assert.rejects(
    runAgent({
      client: clientFrom([
        message("not-json"),
        message(null, [call("repair-tool", "read_file", { path: "x" })]),
      ]),
      config: baseConfig,
      methodologies: [],
      prompt: "p",
      sandbox: { ...sandbox, readFile() { executed = true; } },
      schema,
    }),
    (error) => error.reason === "repair response attempted a tool call",
  );
  assert.equal(executed, false);
});

test("zero-tool configuration never exposes filesystem tools", async () => {
  const requests = [];
  const result = await runAgent({
    client: clientFrom([message('{"answer":"done"}')], requests),
    config: { ...baseConfig, max_tool_calls: 0 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
  });
  assert.equal(result.output, '{"answer":"done"}');
  assert.equal(requests[0].tools, undefined);
  assert.equal(requests[0].tool_choice, undefined);
  assert.deepEqual(requests[0].response_format, { type: "json_object" });
});

test("runtime does not request object-only JSON mode for non-object schemas", async () => {
  const requests = [];
  const result = await runAgent({
    client: clientFrom([message('["done"]')], requests),
    config: { ...baseConfig, max_tool_calls: 0 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema: { type: "array", items: { type: "string" } },
  });
  assert.equal(result.output, '["done"]');
  assert.equal(requests[0].response_format, undefined);
});

test("provider errors are reduced to fixed non-sensitive categories", () => {
  assert.equal(providerFailureReason({ status: 401, message: "secret" }), "provider credential rejected");
  assert.equal(providerFailureReason({ status: 403, message: "secret" }), "provider access forbidden");
  assert.equal(providerFailureReason({ status: 429, message: "secret" }), "provider rate limit reached");
  assert.equal(providerFailureReason({
    status: 429,
    error: { provider_specific_fields: { code: "monthly_cap_reached" } },
  }), "provider quota exhausted");
  assert.equal(providerFailureReason({ status: 408, message: "secret" }), "provider request timed out");
  assert.equal(providerFailureReason({ status: 409, message: "secret" }), "provider request conflict");
  assert.equal(providerFailureReason({ status: 503, message: "secret" }), "provider service unavailable");
  assert.equal(
    providerFailureReason(new APIConnectionTimeoutError({ message: "secret" })),
    "provider request timed out",
  );
  assert.equal(
    providerFailureReason(new APIConnectionError({
      message: "secret",
      cause: new Error("nested secret"),
    })),
    "provider connection failed",
  );
  assert.equal(
    providerFailureReason(new APIUserAbortError({ message: "secret" })),
    "provider request failed",
  );
  class UnknownConnectionError extends APIConnectionError {}
  assert.equal(
    providerFailureReason(new UnknownConnectionError({ message: "secret" })),
    "provider request failed",
  );
  assert.equal(providerFailureReason(new Error("secret")), "provider request failed");
});

test("Retry-After cannot outlive the remaining stage budget", async () => {
  let slept = false;
  const error = Object.assign(new Error("busy"), {
    status: 503,
    headers: new Headers({ "retry-after": "600" }),
  });
  await assert.rejects(
    runAgent({
      client: clientFrom([error]),
      config: { ...baseConfig, max_request_retries: 1, stage_timeout_ms: 100 },
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
      retrySleep: async () => { slept = true; },
    }),
    (failure) => failure.reason === "stage deadline exceeded before the next retry" &&
      failure.category === "limit",
  );
  assert.equal(slept, false);
});

test("real SDK classifies interrupted response bodies as recoverable connections", async () => {
  const metrics = new RuntimeMetrics();
  let calls = 0;
  const client = createProviderClient(OpenAI, {
    apiKey: "test-key",
    baseURL: "https://provider.example/v1",
  }, metrics, async () => {
    calls++;
    return new Response(new ReadableStream({
      start(controller) {
        setTimeout(() => {
          controller.error(Object.assign(new TypeError("terminated"), {
            cause: { code: "UND_ERR_SOCKET" },
          }));
        }, 40);
      },
    }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  });

  await assert.rejects(
    runAgent({
      client, config: baseConfig, methodologies: [], prompt: "p", sandbox, schema, metrics,
    }),
    (error) => error instanceof AgentFailure && error.category === "provider-connection" &&
      error.retryable && error.turnCount === 1,
  );

  assert.equal(calls, 1);
  assert.equal(metrics.snapshot().providerAttempts[0].durationMs >= 40, true);
});

test("real SDK does not treat ordinary EOF as streamed completion", async () => {
  const metrics = new RuntimeMetrics();
  const client = createProviderClient(OpenAI, {
    apiKey: "test-key",
    baseURL: "https://provider.example/v1",
  }, metrics, async () => new Response(
    `data: ${JSON.stringify({
      id: "completion",
      object: "chat.completion.chunk",
      created: 0,
      model: "test",
      choices: [{
        index: 0,
        delta: { content: '{"answer":"truncated"}' },
        finish_reason: null,
      }],
    })}\n\n`,
    { status: 200, headers: { "content-type": "text/event-stream" } },
  ));

  await assert.rejects(
    runAgent({
      client,
      config: { ...baseConfig, max_tool_calls: 0 },
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
      metrics,
    }),
    (error) => error.reason === "provider stream ended without completion metadata" &&
      error.category === "provider-connection" && error.retryable,
  );
});

test("real SDK retries interrupted response bodies within one retry budget", async () => {
  const metrics = new RuntimeMetrics();
  const delays = [];
  let calls = 0;
  const client = createProviderClient(OpenAI, {
    apiKey: "test-key",
    baseURL: "https://provider.example/v1",
  }, metrics, async () => {
    calls++;
    if (calls === 5) {
      return sseResponse('{"answer":"done"}');
    }
    return new Response(new ReadableStream({
      start(controller) {
        queueMicrotask(() => controller.error(Object.assign(new TypeError("terminated"), {
          cause: { code: "UND_ERR_SOCKET" },
        })));
      },
    }), {
      status: 200,
      headers: {
        "content-type": "application/json",
        "retry-after-ms": "0",
      },
    });
  });

  const result = await runAgent({
    client,
    config: { ...baseConfig, max_request_retries: 4 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
    metrics,
    retrySleep: async (milliseconds) => { delays.push(milliseconds); },
  });

  assert.equal(result.turnCount, 1);
  assert.equal(calls, 5);
  assert.equal(delays.every((value, index) => {
    const maximum = 500 * 2 ** index;
    return value >= maximum * 0.75 && value <= maximum;
  }), true);
  assert.equal(metrics.snapshot().requestRetryCount, 4);
  assert.equal(metrics.snapshot().providerAttempts.length, 5);
});

test("real SDK shares retries between status and body failures", async () => {
  const metrics = new RuntimeMetrics();
  const delays = [];
  let calls = 0;
  const client = createProviderClient(OpenAI, {
    apiKey: "test-key",
    baseURL: "https://provider.example/v1",
  }, metrics, async () => {
    calls++;
    if (calls === 1) {
      return new Response("temporary failure", {
        status: 503,
        headers: {
          "content-type": "application/json",
          "retry-after-ms": "0",
          "x-should-retry": "false",
        },
      });
    }
    if (calls === 2) {
      return new Response(new ReadableStream({
        start(controller) {
          queueMicrotask(() => controller.error(Object.assign(new TypeError("terminated"), {
            cause: { code: "UND_ERR_SOCKET" },
          })));
        },
      }), {
        status: 200,
        headers: {
          "content-type": "application/json",
          "retry-after-ms": "0",
        },
      });
    }
    return sseResponse('{"answer":"done"}');
  });

  const result = await runAgent({
    client,
    config: { ...baseConfig, max_request_retries: 4 },
    methodologies: [],
    prompt: "p",
    sandbox,
    schema,
    metrics,
    retrySleep: async (milliseconds) => { delays.push(milliseconds); },
  });

  assert.equal(result.turnCount, 1);
  assert.equal(calls, 3);
  assert.equal(delays[0], 0);
  assert.equal(delays[1] >= 750 && delays[1] <= 1000, true);
  assert.equal(metrics.snapshot().requestRetryCount, 2);
  assert.deepEqual(
    metrics.snapshot().providerAttempts.map((attempt) => attempt.status),
    [503, 200, 200],
  );
});

test("real SDK does not retry policy-terminal responses", async () => {
  for (const [status, body, category] of [
    [401, {}, "provider-credential"],
    [403, {}, "provider-access"],
    [400, {}, "provider-request"],
    [429, { error: { code: "insufficient_quota" } }, "provider-quota"],
  ]) {
    const metrics = new RuntimeMetrics();
    let calls = 0;
    const client = createProviderClient(OpenAI, {
      apiKey: "test-key",
      baseURL: "https://provider.example/v1",
    }, metrics, async () => {
      calls++;
      return new Response(JSON.stringify(body), {
        status,
        headers: {
          "content-type": "application/json",
          "retry-after-ms": "0",
          "x-should-retry": "true",
        },
      });
    });

    await assert.rejects(
      runAgent({
        client, config: baseConfig, methodologies: [], prompt: "p", sandbox, schema, metrics,
      }),
      (error) => error instanceof AgentFailure && error.category === category &&
        !error.retryable && error.turnCount === 1,
    );
    assert.equal(calls, 1);
    assert.equal(metrics.snapshot().requestRetryCount, 0);
  }
});

test("real SDK retries transient responses despite negative provider hints", async () => {
  for (const status of [408, 409, 429, 503]) {
    const metrics = new RuntimeMetrics();
    const delays = [];
    let calls = 0;
    const client = createProviderClient(OpenAI, {
      apiKey: "test-key",
      baseURL: "https://provider.example/v1",
    }, metrics, async () => {
      calls++;
      if (calls === 1) {
        return new Response(JSON.stringify({ error: { code: "temporarily_limited" } }), {
          status,
          headers: {
            "content-type": "application/json",
            "retry-after-ms": "0",
            "x-should-retry": "false",
          },
        });
      }
      return sseResponse('{"answer":"done"}');
    });

    const result = await runAgent({
      client,
      config: { ...baseConfig, max_request_retries: 1 },
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
      metrics,
      retrySleep: async (milliseconds) => { delays.push(milliseconds); },
    });
    assert.equal(result.turnCount, 1);
    assert.equal(calls, 2);
    assert.deepEqual(delays, [0]);
    assert.equal(metrics.snapshot().requestRetryCount, 1);
  }
});

test("real SDK finishes idle attempts after response headers", async () => {
  const metrics = new RuntimeMetrics();
  let responseAdvanced = false;
  const observeResponse = metrics.observeResponse.bind(metrics);
  metrics.observeResponse = (...args) => {
    observeResponse(...args);
    queueMicrotask(() => { responseAdvanced = true; });
  };
  const finishAttempt = metrics.finishAttempt.bind(metrics);
  metrics.finishAttempt = (attempt) => {
    assert.equal(responseAdvanced, true);
    finishAttempt(attempt);
  };
  let calls = 0;
  const client = createProviderClient(OpenAI, {
    apiKey: "test-key",
    baseURL: "https://provider.example/v1",
  }, metrics, async (_url, options) => {
    calls++;
    // The body only ever ends because the stream-idle monitor aborts it.
    return new Response(new ReadableStream({
      start(controller) {
        options.signal.addEventListener("abort", () => controller.error(options.signal.reason));
      },
    }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  });

  await assert.rejects(
    runAgent({
      client,
      config: { ...baseConfig, stream_idle_timeout_ms: 20 },
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
      metrics,
    }),
    (error) => error instanceof AgentFailure && error.category === "provider-timeout" &&
      error.retryable && error.turnCount === 1,
  );

  assert.equal(calls, 1);
  assert.equal(metrics.snapshot().providerAttempts[0].durationMs >= 0, true);
});

test("real SDK preserves known status when an error response body stalls", async () => {
  for (const [status, category, retryable] of [
    [503, "provider-service", true],
    [429, "provider-rate-limit", true],
    [401, "provider-credential", false],
    [400, "provider-request", false],
  ]) {
    const metrics = new RuntimeMetrics();
    let calls = 0;
    const client = createProviderClient(OpenAI, {
      apiKey: "test-key",
      baseURL: "https://provider.example/v1",
    }, metrics, async (_url, options) => {
      calls++;
      return new Response(new ReadableStream({
        start(controller) {
          options.signal.addEventListener("abort", () => controller.error(options.signal.reason));
        },
      }), {
        status,
        headers: { "content-type": "application/json" },
      });
    });

    const started = Date.now();
    await assert.rejects(
      runAgent({
        client,
        config: { ...baseConfig, stream_idle_timeout_ms: 25 },
        methodologies: [],
        prompt: "p",
        sandbox,
        schema,
        metrics,
      }),
      (error) => {
        assert.equal(error instanceof AgentFailure, true);
        assert.equal(error.category, category);
        assert.equal(error.retryable, retryable);
        assert.equal(error.turnCount, 1);
        assert.equal(providerFailureDiagnostic(error.cause).status, status);
        return true;
      },
    );
    const elapsed = Date.now() - started;
    assert.equal(elapsed >= 25, true);
    assert.equal(elapsed < 200, true);
    assert.equal(calls, 1);
    const attempt = metrics.snapshot().providerAttempts[0];
    assert.equal(attempt.status, status);
    assert.equal(attempt.durationMs > 0 && attempt.durationMs <= elapsed, true);
  }
});

test("provider diagnostics expose only bounded status and request IDs", () => {
  assert.deepEqual(providerFailureDiagnostic({
    status: 403,
    requestID: "req_direct-123",
    headers: { get: () => "req_header-456" },
    error: { message: "secret" },
  }), {
    status: 403,
    requestId: "req_direct-123",
  });
  assert.deepEqual(providerFailureDiagnostic({
    status: "429",
    headers: { get: (name) => name === "x-request-id" ? "req_header-456" : null },
  }), {
    status: 429,
    requestId: "req_header-456",
  });
  assert.deepEqual(providerFailureDiagnostic({
    status: 401,
    request_id: "unsafe request id\nsecret",
  }), {
    status: 401,
  });
  assert.equal(providerFailureDiagnostic(new Error("secret")), null);
});

test("executeTool bounds oversized argument strings", () => {
  const result = executeTool(call("large", "read_file", "x".repeat(16 * 1024 + 1)), sandbox);
  assert.match(result, /tool arguments exceed byte limit/);
});

test("repair failures retain the completed output repair count", async () => {
  await assert.rejects(
    runAgent({
      client: clientFrom([
        message('{"wrong":true}'),
        message({ unexpected: true }),
      ]),
      config: baseConfig,
      methodologies: [],
      prompt: "p",
      sandbox,
      schema,
    }),
    (error) => error.reason === "provider stream was malformed" &&
      error.outputRepairCount === 1,
  );
});
