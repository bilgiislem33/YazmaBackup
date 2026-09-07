export class ApiError extends Error {
  status: number;
  correlationId?: string;
  constructor(message: string, status: number, correlationId?: string) {
    super(correlationId ? `${message} · İzleme: ${correlationId}` : message);
    this.status=status;
    this.correlationId=correlationId;
  }
}

function csrfToken() {
  if (typeof document === "undefined") return "";
  return document.cookie.split("; ").find(x => x.startsWith("yb-csrf="))?.split("=")[1] ?? "";
}

export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  const method=(options.method ?? "GET").toUpperCase();
  const headers=new Headers(options.headers);
  if (options.body && !headers.has("Content-Type")) headers.set("Content-Type","application/json");
  if (!["GET","HEAD"].includes(method)) {
    const csrf=csrfToken();
    if (csrf) headers.set("X-YazmaBackup-CSRF",csrf);
  }
  const controller=new AbortController();
  const timer=globalThis.setTimeout(()=>controller.abort(),30000);
  if (options.signal) {
    if (options.signal.aborted) controller.abort();
    else options.signal.addEventListener("abort",()=>controller.abort(),{once:true});
  }
  let response: Response;
  try {
    response=await fetch(path,{...options,headers,signal:controller.signal,credentials:"same-origin",cache:"no-store"});
  } catch (error) {
    if (error instanceof DOMException && error.name==="AbortError") throw new ApiError("API isteği 30 saniye içinde tamamlanmadı",408);
    throw error;
  } finally {
    globalThis.clearTimeout(timer);
  }
  const correlationId=response.headers.get("X-YazmaBackup-Correlation-ID")??undefined;
  if (response.status===204) return undefined as T;
  const text=await response.text();
  let payload: unknown=text;
  if (text) {
    try { payload=JSON.parse(text); } catch { /* keep text */ }
  }
  if (!response.ok) {
    const message=typeof payload==="object" && payload && "error" in payload
      ? String((payload as {error?:unknown}).error ?? `HTTP ${response.status}`)
      : `HTTP ${response.status}`;
    throw new ApiError(message,response.status,correlationId);
  }
  return payload as T;
}
