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

    const response = await fetch(url, options);
    return {
        status: response.status,
        body: await response.text()
    };
}

export function localInputToUtc(value) {
    if (!value) return null;
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

export function utcToLocalInput(value) {
    if (!value) return "";
    const date = new Date(value);
    const pad = number => String(number).padStart(2, "0");
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

export function showDateTimePicker(element) {
    if (!element) return;
    if (typeof element.showPicker === "function") {
        element.showPicker();
        return;
    }
    element.focus();
}

export function formatUtc(value) {
    if (!value) return "—";
    return new Intl.DateTimeFormat(undefined, {
        year: "numeric", month: "2-digit", day: "2-digit",
        hour: "2-digit", minute: "2-digit", hour12: false
    }).format(new Date(value));
}

export function confirmAction(message) {
    return window.confirm(message);
}

export function promptReason(message) {
    return window.prompt(message);
}
