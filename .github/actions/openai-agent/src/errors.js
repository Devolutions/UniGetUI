"use strict";

class ActionError extends Error {
  constructor(code, phase = "configuration") {
    super(code);
    this.name = "ActionError";
    this.code = code;
    this.phase = phase;
  }
}

function fail(code, phase) {
  throw new ActionError(code, phase);
}

module.exports = { ActionError, fail };
