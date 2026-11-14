// ✦ Nova ribbon tab for every desktop Office program (Word, Excel, PowerPoint, Outlook, Visio, Project, Access, Publisher).
// A small native COM add-in (no .NET, no runtime to install), built for 64-bit and 32-bit Office.
// Each button only signals Nova Enhance (one named event per button); Nova does the work on the selection.
// Registered per user by Nova (Settings → Office tab), no admin rights. Designed by A.HAMROUNI

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <ole2.h>
#include <oleauto.h>
#include <string>

// {6E7A2D41-3C9B-4F1E-8A52-1D9C7B3E5F02}
static const CLSID CLSID_NovaRibbon = { 0x6e7a2d41, 0x3c9b, 0x4f1e, { 0x8a, 0x52, 0x1d, 0x9c, 0x7b, 0x3e, 0x5f, 0x02 } };
// IDTExtensibility2 and IRibbonExtensibility (Office), declared here so no Office headers are needed.
static const IID IID_IDTExtensibility2 = { 0xb65ad801, 0xabaf, 0x11d0, { 0xbb, 0x8b, 0x00, 0xa0, 0xc9, 0x0f, 0x27, 0x44 } };
static const IID IID_IRibbonExtensibility = { 0x000c0396, 0x0000, 0x0000, { 0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 } };

struct IDTExtensibility2 : public IDispatch
{
    virtual HRESULT STDMETHODCALLTYPE OnConnection(IDispatch* Application, int ConnectMode, IDispatch* AddInInst, SAFEARRAY** custom) = 0;
    virtual HRESULT STDMETHODCALLTYPE OnDisconnection(int RemoveMode, SAFEARRAY** custom) = 0;
    virtual HRESULT STDMETHODCALLTYPE OnAddInsUpdate(SAFEARRAY** custom) = 0;
    virtual HRESULT STDMETHODCALLTYPE OnStartupComplete(SAFEARRAY** custom) = 0;
    virtual HRESULT STDMETHODCALLTYPE OnBeginShutdown(SAFEARRAY** custom) = 0;
};

struct IRibbonExtensibility : public IDispatch
{
    virtual HRESULT STDMETHODCALLTYPE GetCustomUI(BSTR RibbonID, BSTR* RibbonXml) = 0;
};

static HMODULE g_module = nullptr;
static LONG g_objects = 0, g_locks = 0;

// ------------------------------------------------------------------ talking to Nova

// One named event per button ("NovaEnhanceWindows-office-<button id>"), created by Nova when it runs.
// The add-in only signals it: it never writes files and never starts a program from inside Office.
static void Send(const std::wstring& id)
{
    std::wstring name = L"NovaEnhanceWindows-office-" + id;
    HANDLE e = OpenEventW(EVENT_MODIFY_STATE, FALSE, name.c_str());
    if (e) { SetEvent(e); CloseHandle(e); return; }
    MessageBoxW(GetForegroundWindow(), L"Nova Enhance is not running.\n\nStart it from the Start menu (Nova Enhance), then click the button again.",
                L"\x2726 Nova Enhance", MB_OK | MB_ICONINFORMATION);
}

// ------------------------------------------------------------------ the ribbon

static std::wstring Button(const wchar_t* id, const wchar_t* label, const wchar_t* icon, const wchar_t* tip, bool large)
{
    std::wstring s = L"<button id=\"";
    s += id; s += L"\" label=\""; s += label; s += L"\" imageMso=\""; s += icon; s += L"\" screentip=\""; s += tip;
    s += L"\" size=\""; s += large ? L"large" : L"normal"; s += L"\" onAction=\"OnAction\"/>";
    return s;
}

static std::wstring Xml(bool outlook)
{
    std::wstring x = L"<customUI xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\"><ribbon><tabs>"
                     L"<tab id=\"NovaTab\" label=\"\x2726 Nova\">";
    x += L"<group id=\"NovaWrite\" label=\"Selected text\">";
    x += Button(L"mode_grammar", L"Fix grammar", L"Spelling", L"Fix grammar and spelling of the selected text", true);
    x += Button(L"mode_shorten", L"Shorten", L"FontSizeDecrease", L"Make the selected text shorter", false);
    x += Button(L"mode_professional", L"Professional", L"FormatPainter", L"Rewrite the selection in a professional tone", false);
    x += Button(L"mode_informal", L"Informal", L"HappyFace", L"Rewrite the selection in a friendly tone", false);
    x += Button(L"mode_expand", L"Expand", L"FontSizeIncrease", L"Add detail and flow to the selection", false);
    x += Button(L"mode_translate", L"Translate", L"TranslateMenu", L"Translate the selection (language set in Nova)", false);
    x += Button(L"enhance", L"Nova window", L"AutoFormatChange", L"Open the Nova Enhance window with the selection (Ctrl+Shift+E)", true);
    x += L"</group>";
    if (outlook)
    {
        x += L"<group id=\"NovaMail\" label=\"This email\">";
        x += Button(L"mailask", L"Talk about this email", L"ReplyAll", L"Summary, draft reply and questions about the selected email (Ctrl+Alt+Q)", true);
        x += L"</group>";
    }
    x += L"<group id=\"NovaFind\" label=\"Nova\">";
    x += Button(L"search", L"Search email", L"FindDialog", L"Search your emails, project files and drawings (Ctrl+Shift+F)", true);
    x += Button(L"hub", L"Ask Nova", L"Help", L"Ask Nova about the selection (Ctrl+Shift+A)", true);
    x += Button(L"brief", L"Morning brief", L"DateAndTimeInsert", L"Today's brief: what needs your answer", false);
    x += Button(L"files", L"Find a file", L"FileOpen", L"Find a project file by name", false);
    x += Button(L"settings", L"Settings", L"ControlProperties", L"Nova Enhance settings", false);
    x += L"</group></tab></tabs></ribbon></customUI>";
    return x;
}

// ------------------------------------------------------------------ the add-in object

class AddIn : public IDTExtensibility2, public IRibbonExtensibility
{
    LONG refs = 1;
    bool outlook = false;
public:
    AddIn() { InterlockedIncrement(&g_objects); }
    virtual ~AddIn() { InterlockedDecrement(&g_objects); }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IDispatch || riid == IID_IDTExtensibility2) *ppv = static_cast<IDTExtensibility2*>(this);
        else if (riid == IID_IRibbonExtensibility) *ppv = static_cast<IRibbonExtensibility*>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs); }
    STDMETHODIMP_(ULONG) Release() override { LONG r = InterlockedDecrement(&refs); if (r == 0) delete this; return r; }

    // IDispatch (late-bound calls: GetCustomUI = 1, OnAction = 2)
    STDMETHODIMP GetTypeInfoCount(UINT* n) override { if (n) *n = 0; return S_OK; }
    STDMETHODIMP GetTypeInfo(UINT, LCID, ITypeInfo**) override { return E_NOTIMPL; }
    STDMETHODIMP GetIDsOfNames(REFIID, LPOLESTR* names, UINT count, LCID, DISPID* ids) override
    {
        HRESULT hr = S_OK;
        for (UINT i = 0; i < count; i++)
        {
            if (_wcsicmp(names[i], L"GetCustomUI") == 0) ids[i] = 1;
            else if (_wcsicmp(names[i], L"OnAction") == 0) ids[i] = 2;
            else { ids[i] = DISPID_UNKNOWN; hr = DISP_E_UNKNOWNNAME; }
        }
        return hr;
    }
    STDMETHODIMP Invoke(DISPID id, REFIID, LCID, WORD, DISPPARAMS* p, VARIANT* result, EXCEPINFO*, UINT*) override
    {
        if (id == 1)
        {
            BSTR ribbon = (p && p->cArgs > 0 && p->rgvarg[p->cArgs - 1].vt == VT_BSTR) ? p->rgvarg[p->cArgs - 1].bstrVal : nullptr;
            BSTR xml = nullptr;
            HRESULT hr = GetCustomUI(ribbon, &xml);
            if (result) { VariantInit(result); result->vt = VT_BSTR; result->bstrVal = xml; } else SysFreeString(xml);
            return hr;
        }
        if (id == 2)
        {
            if (!p || p->cArgs < 1) return DISP_E_BADPARAMCOUNT;
            VARIANT& v = p->rgvarg[p->cArgs - 1];
            IDispatch* control = v.vt == VT_DISPATCH ? v.pdispVal : (v.vt == (VT_DISPATCH | VT_BYREF) && v.ppdispVal ? *v.ppdispVal : nullptr);
            if (!control) return E_INVALIDARG;
            // control.Id
            LPOLESTR name = const_cast<LPOLESTR>(L"Id");
            DISPID did = 0;
            if (FAILED(control->GetIDsOfNames(IID_NULL, &name, 1, LOCALE_USER_DEFAULT, &did))) return E_FAIL;
            DISPPARAMS none = { nullptr, nullptr, 0, 0 };
            VARIANT r; VariantInit(&r);
            if (FAILED(control->Invoke(did, IID_NULL, LOCALE_USER_DEFAULT, DISPATCH_PROPERTYGET, &none, &r, nullptr, nullptr)) || r.vt != VT_BSTR) { VariantClear(&r); return E_FAIL; }
            std::wstring cid = r.bstrVal ? r.bstrVal : L"";
            VariantClear(&r);
            Send(cid);
            return S_OK;
        }
        return DISP_E_MEMBERNOTFOUND;
    }

    // IDTExtensibility2
    STDMETHODIMP OnConnection(IDispatch* app, int, IDispatch*, SAFEARRAY**) override
    {
        // Outlook gets the "This email" group (Application.Name = "Outlook").
        if (app)
        {
            LPOLESTR name = const_cast<LPOLESTR>(L"Name");
            DISPID did = 0;
            if (SUCCEEDED(app->GetIDsOfNames(IID_NULL, &name, 1, LOCALE_USER_DEFAULT, &did)))
            {
                DISPPARAMS none = { nullptr, nullptr, 0, 0 };
                VARIANT r; VariantInit(&r);
                if (SUCCEEDED(app->Invoke(did, IID_NULL, LOCALE_USER_DEFAULT, DISPATCH_PROPERTYGET, &none, &r, nullptr, nullptr)) && r.vt == VT_BSTR && r.bstrVal)
                    outlook = wcsstr(r.bstrVal, L"Outlook") != nullptr;
                VariantClear(&r);
            }
        }
        return S_OK;
    }
    STDMETHODIMP OnDisconnection(int, SAFEARRAY**) override { return S_OK; }
    STDMETHODIMP OnAddInsUpdate(SAFEARRAY**) override { return S_OK; }
    STDMETHODIMP OnStartupComplete(SAFEARRAY**) override { return S_OK; }
    STDMETHODIMP OnBeginShutdown(SAFEARRAY**) override { return S_OK; }

    // IRibbonExtensibility
    STDMETHODIMP GetCustomUI(BSTR ribbonId, BSTR* xml) override
    {
        if (!xml) return E_POINTER;
        *xml = nullptr;
        std::wstring id = ribbonId ? ribbonId : L"";
        // Outlook asks once per window type: the main window and opened emails get the tab.
        if (outlook && !id.empty() && id != L"Microsoft.Outlook.Explorer" && id.rfind(L"Microsoft.Outlook.Mail.", 0) != 0) return S_OK;
        *xml = SysAllocString(Xml(outlook).c_str());
        return S_OK;
    }
};

// ------------------------------------------------------------------ class factory + exports

class Factory : public IClassFactory
{
    LONG refs = 1;
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) { *ppv = static_cast<IClassFactory*>(this); AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs); }
    STDMETHODIMP_(ULONG) Release() override { LONG r = InterlockedDecrement(&refs); if (r == 0) delete this; return r; }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        AddIn* a = new (std::nothrow) AddIn();
        if (!a) return E_OUTOFMEMORY;
        HRESULT hr = a->QueryInterface(riid, ppv);
        a->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL lock) override { if (lock) InterlockedIncrement(&g_locks); else InterlockedDecrement(&g_locks); return S_OK; }
};

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (clsid != CLSID_NovaRibbon) return CLASS_E_CLASSNOTAVAILABLE;
    Factory* f = new (std::nothrow) Factory();
    if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

extern "C" HRESULT __stdcall DllCanUnloadNow() { return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE; }

BOOL APIENTRY DllMain(HMODULE m, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { g_module = m; DisableThreadLibraryCalls(m); }
    return TRUE;
}
