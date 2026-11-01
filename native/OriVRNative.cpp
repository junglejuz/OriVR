// OriVRNative: the OpenXR side of OriVR. 32-bit, loaded into the game by the C# plugin.
//
// Threads:
//   Main thread   OriXR_BeginFrame: poll events, xrWaitFrame (paces the game), xrLocateViews.
//   Render thread render events from GL.IssuePluginEvent: create the session, then begin, copy, end each frame.
//
// Each eye image shows the game's flat rectangle seen from that eye. It goes to the headset as a quad layer visible
// to one eye only, placed at a fixed spot in the room: the "stage". Errors are logged, never fatal: a failed OpenXR
// start leaves the game running flat.
#include <windows.h>
#include <d3d11.h>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <vector>
#define XR_NO_PROTOTYPES
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

#define API extern "C" __declspec(dllexport)

enum State { StateOff = 0, StateStarting = 1, StateReady = 2, StateFailed = 3 };
enum Event { EventInit = 1, EventSubmit = 2, EventShutdown = 3, EventResize = 4 };

#pragma pack(push, 4)
struct EyeView { float px, py, pz, qx, qy, qz, qw, left, right, up, down; };
struct FrameInfo {
    int running;        // session is running, so a submit must follow this call
    int shouldRender;   // the runtime wants images
    int viewsValid;     // positions and orientations are usable
    int sessionState;   // XrSessionState
    float periodMs;     // the runtime's frame period
    EyeView eye[2];
};
#pragma pack(pop)

// The eye textures travel with each frame, each holding a reference: Unity can recreate its textures at any time,
// for example when the game changes resolution, so a pointer saved earlier can go stale.
struct PendingFrame { XrTime displayTime; XrDuration period; bool shouldRender; ID3D11Texture2D* src[2]; };

static std::mutex g_logLock;
static std::deque<std::string> g_log;
static void Log(const char* fmt, ...) {
    char buf[512];
    va_list a;
    va_start(a, fmt);
    vsnprintf(buf, sizeof(buf), fmt, a);
    va_end(a);
    std::lock_guard<std::mutex> l(g_logLock);
    if (g_log.size() < 2000) g_log.push_back(buf);
}

static HMODULE g_loader;
static PFN_xrGetInstanceProcAddr g_gipa;
static XrInstance g_inst = XR_NULL_HANDLE;
static XrSystemId g_sys = 0;
static XrSession g_sess = XR_NULL_HANDLE;
static XrSpace g_space = XR_NULL_HANDLE;
static volatile LONG g_state = StateOff;
static volatile LONG g_running = 0;
static volatile LONG g_xrState = 0;
static ID3D11Device* g_dev;
static ID3D11DeviceContext* g_ctx;
static ID3D11Texture2D* g_src[2];
static int g_w, g_h;
static float g_stage[7] = {0, 0, -2, 2, 1.125f, 0, 1};  // centre x, y, z, width, height in metres, turn as quaternion y, w
static int g_recW, g_recH, g_newW, g_newH;
static int64_t g_format;
static PFN_xrCreateSwapchain p_createSwap;
static PFN_xrEnumerateSwapchainImages p_enumImages;
static PFN_xrDestroySwapchain p_destroySwap;
static std::mutex g_frameLock;
static std::deque<PendingFrame> g_frames;
static unsigned long long g_submitted, g_submittedLayers, g_skippedAcquire;

struct Swap { XrSwapchain handle; std::vector<XrSwapchainImageD3D11KHR> images; };
static Swap g_swap[2];

static PFN_xrPollEvent p_poll;
static PFN_xrBeginSession p_beginSession;
static PFN_xrEndSession p_endSession;
static PFN_xrWaitFrame p_waitFrame;
static PFN_xrBeginFrame p_beginFrame;
static PFN_xrEndFrame p_endFrame;
static PFN_xrLocateViews p_locate;
static PFN_xrAcquireSwapchainImage p_acquire;
static PFN_xrWaitSwapchainImage p_waitImage;
static PFN_xrReleaseSwapchainImage p_release;

template <class T> static bool GetFn(const char* name, T& out) {
    PFN_xrVoidFunction f = nullptr;
    XrResult r = g_gipa(g_inst, name, &f);
    if (XR_FAILED(r) || !f) { Log("missing OpenXR function %s (%d)", name, (int)r); return false; }
    out = (T)f;
    return true;
}

#define XRCHECK(call) do { XrResult r_ = (call); if (XR_FAILED(r_)) { Log("%s failed: %d", #call, (int)r_); return false; } } while (0)

static void HereDir(char* path, DWORD size) {
    HMODULE self = nullptr;
    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)&HereDir, &self);
    GetModuleFileNameA(self, path, size);
    char* slash = strrchr(path, '\\');
    if (slash) slash[1] = 0;
}

static bool CreateSwapchains() {
    for (int e = 0; e < 2; e++) {
        XrSwapchainCreateInfo c{XR_TYPE_SWAPCHAIN_CREATE_INFO};
        c.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
        c.format = g_format;
        c.sampleCount = 1;
        c.width = g_w;
        c.height = g_h;
        c.faceCount = c.arraySize = c.mipCount = 1;
        XRCHECK(p_createSwap(g_sess, &c, &g_swap[e].handle));
        uint32_t ic = 0;
        XRCHECK(p_enumImages(g_swap[e].handle, 0, &ic, nullptr));
        g_swap[e].images.assign(ic, XrSwapchainImageD3D11KHR{XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
        XRCHECK(p_enumImages(g_swap[e].handle, ic, &ic, (XrSwapchainImageBaseHeader*)g_swap[e].images.data()));
    }
    Log("swapchains %dx%d, %zu images per eye", g_w, g_h, g_swap[0].images.size());
    return true;
}

static void DoResize() {
    for (auto& sw : g_swap) if (sw.handle) { p_destroySwap(sw.handle); sw.handle = XR_NULL_HANDLE; }
    g_w = g_newW;
    g_h = g_newH;
    if (!CreateSwapchains()) { Log("resize failed, OpenXR stops"); g_state = StateFailed; }
}

static bool StartXr() {
    char path[MAX_PATH];
    HereDir(path, MAX_PATH);
    strcat_s(path, "openxr_loader.dll");
    g_loader = LoadLibraryA(path);
    if (!g_loader) { Log("could not load %s, error %lu", path, GetLastError()); return false; }
    g_gipa = (PFN_xrGetInstanceProcAddr)GetProcAddress(g_loader, "xrGetInstanceProcAddr");
    if (!g_gipa) { Log("loader has no xrGetInstanceProcAddr"); return false; }

    PFN_xrCreateInstance createInstance;
    if (!GetFn("xrCreateInstance", createInstance)) return false;
    const char* exts[] = {XR_KHR_D3D11_ENABLE_EXTENSION_NAME};
    XrInstanceCreateInfo ici{XR_TYPE_INSTANCE_CREATE_INFO};
    strcpy_s(ici.applicationInfo.applicationName, "OriVR");
    ici.applicationInfo.applicationVersion = 1;
    strcpy_s(ici.applicationInfo.engineName, "Unity 5.3");
    ici.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
    ici.enabledExtensionCount = 1;
    ici.enabledExtensionNames = exts;
    XRCHECK(createInstance(&ici, &g_inst));

    PFN_xrGetInstanceProperties props;
    if (GetFn("xrGetInstanceProperties", props)) {
        XrInstanceProperties ip{XR_TYPE_INSTANCE_PROPERTIES};
        if (XR_SUCCEEDED(props(g_inst, &ip)))
            Log("runtime %s %u.%u.%u", ip.runtimeName, XR_VERSION_MAJOR(ip.runtimeVersion), XR_VERSION_MINOR(ip.runtimeVersion), XR_VERSION_PATCH(ip.runtimeVersion));
    }

    PFN_xrGetSystem getSystem;
    if (!GetFn("xrGetSystem", getSystem)) return false;
    XrSystemGetInfo sgi{XR_TYPE_SYSTEM_GET_INFO};
    sgi.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XRCHECK(getSystem(g_inst, &sgi, &g_sys));

    PFN_xrGetD3D11GraphicsRequirementsKHR req11;
    if (!GetFn("xrGetD3D11GraphicsRequirementsKHR", req11)) return false;
    XrGraphicsRequirementsD3D11KHR req{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
    XRCHECK(req11(g_inst, g_sys, &req));
    IDXGIDevice* dxgi = nullptr;
    IDXGIAdapter* adapter = nullptr;
    if (SUCCEEDED(g_dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgi)) && SUCCEEDED(dxgi->GetAdapter(&adapter))) {
        DXGI_ADAPTER_DESC d;
        adapter->GetDesc(&d);
        Log("game adapter %ls LUID %08lx:%08lx, runtime wants %08lx:%08lx", d.Description, (unsigned long)d.AdapterLuid.HighPart, d.AdapterLuid.LowPart,
            (unsigned long)req.adapterLuid.HighPart, req.adapterLuid.LowPart);
        bool zero = req.adapterLuid.HighPart == 0 && req.adapterLuid.LowPart == 0;  // RigB's null driver reports zero
        if (!zero && (d.AdapterLuid.HighPart != req.adapterLuid.HighPart || d.AdapterLuid.LowPart != req.adapterLuid.LowPart))
            Log("WARNING: the headset's GPU is not the one the game runs on");
        adapter->Release();
    }
    if (dxgi) dxgi->Release();

    PFN_xrCreateSession createSession;
    if (!GetFn("xrCreateSession", createSession)) return false;
    XrGraphicsBindingD3D11KHR bind{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR};
    bind.device = g_dev;
    XrSessionCreateInfo sci{XR_TYPE_SESSION_CREATE_INFO};
    sci.next = &bind;
    sci.systemId = g_sys;
    XRCHECK(createSession(g_inst, &sci, &g_sess));

    PFN_xrCreateReferenceSpace createSpace;
    if (!GetFn("xrCreateReferenceSpace", createSpace)) return false;
    XrReferenceSpaceCreateInfo rsci{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    rsci.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    rsci.poseInReferenceSpace.orientation.w = 1.0f;
    XRCHECK(createSpace(g_sess, &rsci, &g_space));

    D3D11_TEXTURE2D_DESC td;
    g_src[0]->GetDesc(&td);
    Log("eye texture %ux%u format %d", td.Width, td.Height, (int)td.Format);

    PFN_xrEnumerateSwapchainFormats enumFormats;
    if (!GetFn("xrEnumerateSwapchainFormats", enumFormats)) return false;
    uint32_t n = 0;
    XRCHECK(enumFormats(g_sess, 0, &n, nullptr));
    std::vector<int64_t> formats(n);
    XRCHECK(enumFormats(g_sess, n, &n, formats.data()));
    // The game draws in gamma space. Copying those bytes into an sRGB-labelled image makes the compositor decode them
    // correctly. The two formats share one typeless family, so the copy is a plain byte copy.
    int64_t format = 0;
    for (auto f : formats) if (f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB) format = f;
    if (!format) for (auto f : formats) if (f == DXGI_FORMAT_R8G8B8A8_UNORM) format = f;
    if (!format) { Log("the runtime offers no RGBA8 swapchain format"); return false; }
    Log("swapchain format %d", (int)format);

    g_format = format;
    if (!GetFn("xrCreateSwapchain", p_createSwap) || !GetFn("xrEnumerateSwapchainImages", p_enumImages) || !GetFn("xrDestroySwapchain", p_destroySwap)) return false;

    PFN_xrEnumerateViewConfigurationViews enumViews;
    if (GetFn("xrEnumerateViewConfigurationViews", enumViews)) {
        XrViewConfigurationView v[2] = {XrViewConfigurationView{XR_TYPE_VIEW_CONFIGURATION_VIEW}, XrViewConfigurationView{XR_TYPE_VIEW_CONFIGURATION_VIEW}};
        uint32_t vc = 0;
        if (XR_SUCCEEDED(enumViews(g_inst, g_sys, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 2, &vc, v))) {
            g_recW = (int)v[0].recommendedImageRectWidth;
            g_recH = (int)v[0].recommendedImageRectHeight;
            Log("headset recommends %dx%d per eye", g_recW, g_recH);
        }
    }
    if (!CreateSwapchains()) return false;

    return GetFn("xrPollEvent", p_poll) && GetFn("xrBeginSession", p_beginSession) && GetFn("xrEndSession", p_endSession) &&
           GetFn("xrWaitFrame", p_waitFrame) && GetFn("xrBeginFrame", p_beginFrame) && GetFn("xrEndFrame", p_endFrame) &&
           GetFn("xrLocateViews", p_locate) && GetFn("xrAcquireSwapchainImage", p_acquire) &&
           GetFn("xrWaitSwapchainImage", p_waitImage) && GetFn("xrReleaseSwapchainImage", p_release);
}

static void DoInit() {
    if (!g_src[0] || !g_src[1]) { Log("no eye textures set"); g_state = StateFailed; return; }
    g_src[0]->GetDevice(&g_dev);
    g_dev->GetImmediateContext(&g_ctx);
    bool ok = StartXr();
    g_state = ok ? StateReady : StateFailed;
    Log(ok ? "OpenXR ready" : "OpenXR start failed, the game stays flat");
}

static void ReleaseFrame(PendingFrame& f) {
    for (auto& t : f.src) { if (t) t->Release(); t = nullptr; }
}

static bool CopyEye(int e, ID3D11Texture2D* src, XrCompositionLayerQuad& quad) {
    D3D11_TEXTURE2D_DESC td;
    if (!src) { Log("eye %d has no texture this frame", e); return false; }
    src->GetDesc(&td);
    if ((int)td.Width != g_w || (int)td.Height != g_h) { Log("eye %d texture is %ux%u, expected %dx%d", e, td.Width, td.Height, g_w, g_h); return false; }
    uint32_t idx = 0;
    XrResult r = p_acquire(g_swap[e].handle, nullptr, &idx);
    if (XR_FAILED(r)) { Log("acquire failed: %d", (int)r); return false; }
    XrSwapchainImageWaitInfo wi{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
    wi.timeout = 100000000;  // 100 ms
    r = p_waitImage(g_swap[e].handle, &wi);
    if (r != XR_SUCCESS) { Log("wait image: %d", (int)r); if (XR_FAILED(r)) return false; }
    g_ctx->CopySubresourceRegion(g_swap[e].images[idx].texture, 0, 0, 0, 0, src, 0, nullptr);
    p_release(g_swap[e].handle, nullptr);

    quad = XrCompositionLayerQuad{XR_TYPE_COMPOSITION_LAYER_QUAD};
    quad.space = g_space;
    quad.eyeVisibility = e == 0 ? XR_EYE_VISIBILITY_LEFT : XR_EYE_VISIBILITY_RIGHT;
    quad.subImage.swapchain = g_swap[e].handle;
    quad.subImage.imageRect.extent = {g_w, g_h};
    quad.pose.orientation.y = g_stage[5];
    quad.pose.orientation.w = g_stage[6];
    quad.pose.position = {g_stage[0], g_stage[1], g_stage[2]};
    quad.size = {g_stage[3], g_stage[4]};
    return true;
}

static void DoSubmit() {
    PendingFrame f;
    {
        std::lock_guard<std::mutex> l(g_frameLock);
        if (g_frames.empty()) return;
        f = g_frames.front();
        g_frames.pop_front();
    }
    XrResult r = p_beginFrame(g_sess, nullptr);
    if (XR_FAILED(r)) { Log("xrBeginFrame failed: %d", (int)r); ReleaseFrame(f); return; }
    XrCompositionLayerQuad quads[2];
    const XrCompositionLayerBaseHeader* layers[2];
    uint32_t count = 0;
    if (f.shouldRender) {
        if (CopyEye(0, f.src[0], quads[0]) && CopyEye(1, f.src[1], quads[1])) {
            layers[0] = (XrCompositionLayerBaseHeader*)&quads[0];
            layers[1] = (XrCompositionLayerBaseHeader*)&quads[1];
            count = 2;
        } else {
            g_skippedAcquire++;
        }
    }
    XrFrameEndInfo fei{XR_TYPE_FRAME_END_INFO};
    fei.displayTime = f.displayTime;
    fei.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
    fei.layerCount = count;
    fei.layers = layers;
    r = p_endFrame(g_sess, &fei);
    ReleaseFrame(f);
    if (XR_FAILED(r)) { Log("xrEndFrame failed: %d", (int)r); return; }
    g_submitted++;
    if (count) g_submittedLayers++;
}

static void DoShutdown() {
    Log("shutting down, frames %llu, with images %llu, skipped %llu", g_submitted, g_submittedLayers, g_skippedAcquire);
    if (g_inst) {
        PFN_xrDestroyInstance destroy;
        if (GetFn("xrDestroyInstance", destroy)) destroy(g_inst);  // takes the session, spaces and swapchains with it
    }
    for (auto& sw : g_swap) sw.handle = XR_NULL_HANDLE;
    g_inst = XR_NULL_HANDLE;
    g_sess = XR_NULL_HANDLE;
    g_space = XR_NULL_HANDLE;
    g_running = 0;
    g_state = StateOff;
    std::lock_guard<std::mutex> l(g_frameLock);
    for (auto& f : g_frames) ReleaseFrame(f);
    g_frames.clear();
}

static void __stdcall RenderEvent(int id) {
    if (id == EventInit && g_state == StateStarting) DoInit();
    else if (id == EventSubmit && g_state == StateReady) DoSubmit();
    else if (id == EventResize && g_state == StateReady) DoResize();
    else if (id == EventShutdown && g_state != StateOff) DoShutdown();
}

API void* OriXR_GetRenderEvent() { return (void*)RenderEvent; }
API int OriXR_State() { return (int)g_state; }

API void OriXR_SetStage(float cx, float cy, float cz, float width, float height, float qy, float qw) {
    g_stage[0] = cx; g_stage[1] = cy; g_stage[2] = cz; g_stage[3] = width; g_stage[4] = height; g_stage[5] = qy; g_stage[6] = qw;
}

// Takes effect with the next resize event on the render thread.
API void OriXR_SetSize(int width, int height) { g_newW = width; g_newH = height; }
API int OriXR_RecommendedWidth() { return g_recW; }

// Call on the main thread before the init event. The textures must already exist in the Unity player.
API void OriXR_Start(void* left, void* right, int width, int height) {
    if (g_state != StateOff && g_state != StateFailed) return;
    g_src[0] = (ID3D11Texture2D*)left;
    g_src[1] = (ID3D11Texture2D*)right;
    g_w = width;
    g_h = height;
    g_state = StateStarting;
}

API int OriXR_BeginFrame(FrameInfo* out, void* left, void* right) {
    memset(out, 0, sizeof(*out));
    out->sessionState = (int)g_xrState;
    if (g_state != StateReady) return 0;
    XrEventDataBuffer ev{XR_TYPE_EVENT_DATA_BUFFER};
    while (p_poll(g_inst, &ev) == XR_SUCCESS) {
        if (ev.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
            auto* s = (XrEventDataSessionStateChanged*)&ev;
            g_xrState = (LONG)s->state;
            Log("session state %d", (int)s->state);
            if (s->state == XR_SESSION_STATE_READY) {
                XrSessionBeginInfo bi{XR_TYPE_SESSION_BEGIN_INFO};
                bi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                XrResult r = p_beginSession(g_sess, &bi);
                Log("xrBeginSession: %d", (int)r);
                if (XR_SUCCEEDED(r)) g_running = 1;
            } else if (s->state == XR_SESSION_STATE_STOPPING) {
                g_running = 0;
                p_endSession(g_sess);
            }
        } else if (ev.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) {
            Log("the runtime is going away");
        }
        ev = XrEventDataBuffer{XR_TYPE_EVENT_DATA_BUFFER};
    }
    out->sessionState = (int)g_xrState;
    if (!g_running) return 0;

    XrFrameState fs{XR_TYPE_FRAME_STATE};
    XrResult r = p_waitFrame(g_sess, nullptr, &fs);
    if (XR_FAILED(r)) { Log("xrWaitFrame failed: %d", (int)r); return 0; }
    out->running = 1;
    out->shouldRender = fs.shouldRender ? 1 : 0;
    out->periodMs = (float)(fs.predictedDisplayPeriod / 1e6);
    {
        std::lock_guard<std::mutex> l(g_frameLock);
        PendingFrame f{fs.predictedDisplayTime, fs.predictedDisplayPeriod, fs.shouldRender != 0, {(ID3D11Texture2D*)left, (ID3D11Texture2D*)right}};
        for (auto t : f.src) if (t) t->AddRef();
        g_frames.push_back(f);
    }
    if (fs.shouldRender) {
        XrViewLocateInfo li{XR_TYPE_VIEW_LOCATE_INFO};
        li.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
        li.displayTime = fs.predictedDisplayTime;
        li.space = g_space;
        XrViewState vs{XR_TYPE_VIEW_STATE};
        XrView views[2] = {XrView{XR_TYPE_VIEW}, XrView{XR_TYPE_VIEW}};
        uint32_t vc = 0;
        r = p_locate(g_sess, &li, &vs, 2, &vc, views);
        if (XR_SUCCEEDED(r) && vc == 2 && (vs.viewStateFlags & XR_VIEW_STATE_POSITION_VALID_BIT)) {
            out->viewsValid = 1;
            for (int e = 0; e < 2; e++) {
                auto& p = views[e].pose;
                out->eye[e] = {p.position.x, p.position.y, p.position.z, p.orientation.x, p.orientation.y, p.orientation.z, p.orientation.w,
                               views[e].fov.angleLeft, views[e].fov.angleRight, views[e].fov.angleUp, views[e].fov.angleDown};
            }
        }
    }
    return 1;
}

API int OriXR_PopLog(char* buffer, int size) {
    std::lock_guard<std::mutex> l(g_logLock);
    if (g_log.empty()) return 0;
    strncpy_s(buffer, size, g_log.front().c_str(), _TRUNCATE);
    g_log.pop_front();
    return 1;
}

API void OriXR_Stats(unsigned int* frames, unsigned int* layers, unsigned int* skipped) {
    *frames = (unsigned int)g_submitted;
    *layers = (unsigned int)g_submittedLayers;
    *skipped = (unsigned int)g_skippedAcquire;
}
