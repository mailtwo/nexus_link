(function () {
  if (typeof window === "undefined" || typeof window.Worker === "undefined") {
    return;
  }

  var NativeWorker = window.Worker;
  if (NativeWorker.__uplinkSearchQueryPatched) {
    return;
  }

  function normalizeDocfxSearchQuery(query) {
    return query
      .split(/\s+/)
      .filter(function (token) {
        return token.length > 0;
      })
      .flatMap(function (token) {
        var hasPrefix = token.startsWith("+") || token.startsWith("-");
        var prefix = hasPrefix ? token[0] : "";
        var body = hasPrefix ? token.slice(1) : token;

        var parts = body
          .replace(/[().,/:#\[\]{}<>?!'"`~@%^&*=|\\]+/g, " ")
          .replace(/\s+/g, " ")
          .trim()
          .split(" ")
          .filter(function (part) {
            return part.length > 0;
          });

        if (parts.length === 0) {
          return [token];
        }

        return parts.map(function (part) {
          return prefix + part;
        });
      })
      .join(" ");
  }

  class WorkerWithSearchQueryPatch extends NativeWorker {
    constructor(url, options) {
      super(url, options);
      this._isDocfxSearchWorker =
        typeof url === "string" && url.indexOf("search-worker.min.js") >= 0;
    }

    postMessage(message, transferOrOptions) {
      if (
        this._isDocfxSearchWorker &&
        message &&
        typeof message === "object" &&
        typeof message.q === "string"
      ) {
        var normalizedQuery = normalizeDocfxSearchQuery(message.q);
        if (normalizedQuery !== message.q) {
          message = Object.assign({}, message, { q: normalizedQuery });
        }
      }

      return super.postMessage(message, transferOrOptions);
    }
  }

  WorkerWithSearchQueryPatch.__uplinkSearchQueryPatched = true;
  window.Worker = WorkerWithSearchQueryPatch;
})();
