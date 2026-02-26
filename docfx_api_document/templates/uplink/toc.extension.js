// Force stable module labels in TOC output regardless of metadata-generated toc.yml names.

const moduleNameByUid = {
  "Uplink2.Runtime.MiniScript.MiniScriptSshIntrinsics": "Module ssh",
  "Uplink2.Runtime.MiniScript.MiniScriptFtpIntrinsics": "Module ftp",
  "Uplink2.Runtime.MiniScript.MiniScriptNetIntrinsics": "Module net",
  "Uplink2.Runtime.MiniScript.MiniScriptTermIntrinsics": "Module term",
  "Uplink2.Runtime.MiniScript.MiniScriptFsIntrinsics": "Module fs",
};

const moduleOrderByUid = {
  "Uplink2.Runtime.MiniScript.MiniScriptSshIntrinsics": 10,
  "Uplink2.Runtime.MiniScript.MiniScriptFtpIntrinsics": 20,
  "Uplink2.Runtime.MiniScript.MiniScriptNetIntrinsics": 30,
  "Uplink2.Runtime.MiniScript.MiniScriptTermIntrinsics": 40,
  "Uplink2.Runtime.MiniScript.MiniScriptFsIntrinsics": 50,
};

const moduleNameByTopicFile = {
  "Uplink2.Runtime.MiniScript.MiniScriptSshIntrinsics.html": "Module ssh",
  "Uplink2.Runtime.MiniScript.MiniScriptFtpIntrinsics.html": "Module ftp",
  "Uplink2.Runtime.MiniScript.MiniScriptNetIntrinsics.html": "Module net",
  "Uplink2.Runtime.MiniScript.MiniScriptTermIntrinsics.html": "Module term",
  "Uplink2.Runtime.MiniScript.MiniScriptFsIntrinsics.html": "Module fs",
};

function getFileNameFromHref(href) {
  if (typeof href !== "string" || href.length === 0) {
    return "";
  }

  const hashIndex = href.indexOf("#");
  const hrefWithoutHash = hashIndex >= 0 ? href.slice(0, hashIndex) : href;
  const queryIndex = hrefWithoutHash.indexOf("?");
  const hrefWithoutQuery = queryIndex >= 0 ? hrefWithoutHash.slice(0, queryIndex) : hrefWithoutHash;

  const lastSlash = Math.max(hrefWithoutQuery.lastIndexOf("/"), hrefWithoutQuery.lastIndexOf("\\"));
  return lastSlash >= 0 ? hrefWithoutQuery.slice(lastSlash + 1) : hrefWithoutQuery;
}

function resolveModuleName(item) {
  const uidCandidates = [item && item.uid, item && item.topicUid];
  for (let index = 0; index < uidCandidates.length; index++) {
    const candidate = uidCandidates[index];
    if (typeof candidate === "string" &&
        Object.prototype.hasOwnProperty.call(moduleNameByUid, candidate)) {
      return moduleNameByUid[candidate];
    }
  }

  const hrefCandidates = [item && item.href, item && item.topicHref, item && item.tocHref];
  for (let index = 0; index < hrefCandidates.length; index++) {
    const topicFile = getFileNameFromHref(hrefCandidates[index]);
    if (topicFile.length > 0 &&
        Object.prototype.hasOwnProperty.call(moduleNameByTopicFile, topicFile)) {
      return moduleNameByTopicFile[topicFile];
    }
  }

  return "";
}

function resolveModuleOrder(item) {
  const uidCandidates = [item && item.uid, item && item.topicUid];
  for (let index = 0; index < uidCandidates.length; index++) {
    const candidate = uidCandidates[index];
    if (typeof candidate === "string" &&
        Object.prototype.hasOwnProperty.call(moduleOrderByUid, candidate)) {
      return moduleOrderByUid[candidate];
    }
  }

  return -1;
}

function reorderModuleItems(items) {
  if (!Array.isArray(items) || items.length === 0) {
    return;
  }

  const decorated = items.map((value, index) => ({
    value,
    index,
    order: resolveModuleOrder(value),
  }));

  decorated.sort((left, right) => {
    const leftHasOrder = left.order >= 0;
    const rightHasOrder = right.order >= 0;
    if (leftHasOrder && rightHasOrder) {
      if (left.order !== right.order) {
        return left.order - right.order;
      }

      return left.index - right.index;
    }

    if (leftHasOrder !== rightHasOrder) {
      return leftHasOrder ? -1 : 1;
    }

    return left.index - right.index;
  });

  for (let index = 0; index < decorated.length; index++) {
    items[index] = decorated[index].value;
  }
}

function normalizeTocItemNames(item) {
  if (!item || typeof item !== "object") {
    return;
  }

  const moduleName = resolveModuleName(item);
  if (moduleName.length > 0) {
    item.name = moduleName;
  }

  if (Array.isArray(item.items)) {
    for (let index = 0; index < item.items.length; index++) {
      normalizeTocItemNames(item.items[index]);
    }

    reorderModuleItems(item.items);
  }
}

exports.preTransform = function (model) {
  normalizeTocItemNames(model);
  return model;
};

exports.postTransform = function (model) {
  normalizeTocItemNames(model);
  return model;
};
