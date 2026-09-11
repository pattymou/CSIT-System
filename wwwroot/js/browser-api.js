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

export async function uploadFiles(url, inputContainer) {
    const input = inputContainer?.querySelector('input[type="file"]');
    if (!input || input.files.length === 0) {
        return { status: 400, body: "No files were selected." };
    }

    const formData = new FormData();
    for (const file of input.files) {
        formData.append("files", file, file.name);
    }

    const response = await fetch(url, {
        method: "POST",
        credentials: "same-origin",
        headers: { "Accept": "application/json" },
        body: formData
    });

    if (response.ok) input.value = "";
    return toResult(response);
}

async function toResult(response) {
    return {
        status: response.status,
        body: await response.text()
    };
}
