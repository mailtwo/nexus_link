// Custom managed-reference transforms for MiniScript API docs.

const miniScriptModulePrefixes = [
  "Uplink2.Runtime.MiniScript.MiniScriptSshIntrinsics",
  "Uplink2.Runtime.MiniScript.MiniScriptFtpIntrinsics",
  "Uplink2.Runtime.MiniScript.MiniScriptNetIntrinsics",
  "Uplink2.Runtime.MiniScript.MiniScriptTermIntrinsics",
  "Uplink2.Runtime.MiniScript.MiniScriptFsIntrinsics",
];

const moduleInfoByClassUid = {
  "Uplink2.Runtime.MiniScript.MiniScriptSshIntrinsics": {
    title: "Module ssh",
  },
  "Uplink2.Runtime.MiniScript.MiniScriptFtpIntrinsics": {
    title: "Module ftp",
  },
  "Uplink2.Runtime.MiniScript.MiniScriptNetIntrinsics": {
    title: "Module net",
  },
  "Uplink2.Runtime.MiniScript.MiniScriptTermIntrinsics": {
    title: "Module term",
  },
  "Uplink2.Runtime.MiniScript.MiniScriptFsIntrinsics": {
    title: "Module fs",
  },
};

function isMiniScriptModuleUid(uid) {
  if (!uid) {
    return false;
  }

  for (let index = 0; index < miniScriptModulePrefixes.length; index++) {
    const prefix = miniScriptModulePrefixes[index];
    if (uid === prefix || uid.startsWith(prefix + ".")) {
      return true;
    }
  }

  return false;
}

function markMiniScriptSyntax(node) {
  if (!node || typeof node !== "object") {
    return;
  }

  if (typeof node.uid === "string" &&
      typeof node.type === "string" &&
      node.type.toLowerCase() === "method" &&
      isMiniScriptModuleUid(node.uid)) {
    node._isMiniScriptSyntax = true;

    // Move conceptual custom anchors (e.g. <a id="sshconnect"></a>) to the
    // method header location so hash navigation lands at the title line.
    if (typeof node.conceptual === "string" && node.conceptual.length > 0) {
      const anchorMatch = node.conceptual.match(/<a\s+id="([^"]+)"\s*><\/a>/i);
      if (anchorMatch && anchorMatch[1]) {
        node._manualAnchorId = anchorMatch[1];
        const conceptualWithoutAnchor = node.conceptual
          .replace(/<a\s+id="[^"]+"\s*><\/a>/ig, "")
          .trim();
        const conceptualPlainText = conceptualWithoutAnchor
          .replace(/<[^>]+>/g, "")
          .replace(/&nbsp;/ig, " ")
          .trim();
        node._hideConceptual = conceptualPlainText.length === 0;
        node.conceptual = conceptualPlainText.length > 0 ? conceptualWithoutAnchor : null;
      }
    }
  }

  if (Array.isArray(node.children)) {
    for (let index = 0; index < node.children.length; index++) {
      markMiniScriptSyntax(node.children[index]);
    }
  }
}

exports.postTransform = function (model) {
  if (!model || typeof model !== "object") {
    return model;
  }

  if (typeof model.uid === "string" &&
      typeof model.type === "string" &&
      model.type.toLowerCase() === "class" &&
      Object.prototype.hasOwnProperty.call(moduleInfoByClassUid, model.uid)) {
    const moduleInfo = moduleInfoByClassUid[model.uid];
    model._isMiniScriptModulePage = true;
    model._moduleTitle = moduleInfo.title;
  }

  markMiniScriptSyntax(model);

  return model;
};
