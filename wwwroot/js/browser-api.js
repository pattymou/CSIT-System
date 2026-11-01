export async function request(method, url, body) {
    const options = {
        method,
        credentials: "same-origin",
        headers: { "Accept": "application/json" }
    };

    if (body !== null && body !== undefined) {
        options.headers["Content-Type"] = "application/json";
        options.body = JSON.stringify(body);
    }

    return toResult(await fetch(url, options));
}

export async function uploadFiles(url, inputContainer, fileNames = null, clearInput = true) {
    const input = inputContainer?.querySelector('input[type="file"]');
    if (!input || input.files.length === 0) {
        return { status: 400, body: "No files were selected." };
    }

    const formData = new FormData();
    for (const file of input.files) {
        if (!fileNames || fileNames.includes(file.name)) {
            formData.append("files", file, file.name);
        }
    }

    if (!formData.has("files")) return { status: 400, body: "No matching files were selected." };

    const response = await fetch(url, {
        method: "POST",
        credentials: "same-origin",
        headers: { "Accept": "application/json" },
        body: formData
    });

    if (response.ok && clearInput) input.value = "";
    return toResult(response);
}

const accumulatedUploadFiles = new WeakMap();

export function accumulateUploadFiles(inputContainer, maxFiles = 20) {
    const input = inputContainer?.querySelector('input[type="file"]');
    const state = getAccumulatedUploadState(inputContainer);
    const duplicateNames = [];
    const rejectedNames = [];

    if (!input) {
        return selectionResult(state, duplicateNames, rejectedNames, maxFiles);
    }

    for (const file of input.files) {
        const normalizedName = file.name.trim().toLowerCase();
        if (state.files.some(item => item.file.name.trim().toLowerCase() === normalizedName)) {
            duplicateNames.push(file.name);
            continue;
        }

        if (state.files.length >= maxFiles) {
            rejectedNames.push(file.name);
            continue;
        }

        state.files.push({ id: createUploadFileId(), file });
    }

    input.value = "";
    return selectionResult(state, duplicateNames, rejectedNames, maxFiles);
}

export function removeAccumulatedUploadFile(inputContainer, fileId, maxFiles = 20) {
    const state = getAccumulatedUploadState(inputContainer);
    state.files = state.files.filter(item => item.id !== fileId);
    return selectionResult(state, [], [], maxFiles);
}

export function clearAccumulatedUploadFiles(inputContainer) {
    const input = inputContainer?.querySelector('input[type="file"]');
    if (inputContainer) accumulatedUploadFiles.delete(inputContainer);
    if (input) input.value = "";
}

export async function uploadAccumulatedFiles(url, inputContainer, clearOnSuccess = true) {
    const state = getAccumulatedUploadState(inputContainer);
    if (state.files.length === 0) {
        return { status: 400, body: "No files were selected." };
    }

    const formData = new FormData();
    for (const item of state.files) {
        formData.append("files", item.file, item.file.name);
    }

    const response = await fetch(url, {
        method: "POST",
        credentials: "same-origin",
        headers: { "Accept": "application/json" },
        body: formData
    });

    if (response.ok && clearOnSuccess) clearAccumulatedUploadFiles(inputContainer);
    return toResult(response);
}

function getAccumulatedUploadState(inputContainer) {
    if (!inputContainer) return { files: [] };

    let state = accumulatedUploadFiles.get(inputContainer);
    if (!state) {
        state = { files: [] };
        accumulatedUploadFiles.set(inputContainer, state);
    }
    return state;
}

function selectionResult(state, duplicateNames, rejectedNames, maxFiles) {
    return {
        files: state.files.map(item => ({
            id: item.id,
            fileName: item.file.name,
            contentType: item.file.type,
            size: item.file.size
        })),
        duplicateNames,
        rejectedNames,
        maxFiles
    };
}

function createUploadFileId() {
    return globalThis.crypto?.randomUUID?.()
        ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

async function toResult(response) {
    return {
        status: response.status,
        body: await response.text()
    };
}
