---@meta
--- Canonical EmmyLua type definitions for ProjectKongor K2 Engine (Heroes of Newerth x64)
--- Runtime: Vanilla 64-bit Lua 5.1

---@class CWidget
---@field Name string
---@field Visible boolean
---@field Enabled boolean
---@field Text string
---@field Texture string
---@field Color string
---@field X number
---@field Y number
---@field Width number
---@field Height number
local CWidget = {}

--- Returns the name of the widget
---@return string
function CWidget:GetName() end

--- Returns the current text content of the widget
---@return string
function CWidget:GetText() end

--- Sets the text content of the widget
---@param text string|number
function CWidget:SetText(text) end

--- Sets text with safe string conversion
---@param text string|number
function CWidget:SetSafeText(text) end

--- Gets widget value
---@return string
function CWidget:GetValue() end

--- Sets widget value
---@param val string|number
function CWidget:SetValue(val) end

--- Sets maximum value for scrollbars/sliders
---@param val string|number
function CWidget:SetMaxValue(val) end

--- Gets absolute screen X coordinate
---@return number
function CWidget:GetAbsoluteX() end

--- Gets absolute screen Y coordinate
---@return number
function CWidget:GetAbsoluteY() end

--- Gets widget width in pixels
---@return number
function CWidget:GetWidth() end

--- Gets widget height in pixels
---@return number
function CWidget:GetHeight() end

--- Sets widget width (accepts pixel numbers or CSS/K2 dimension strings like '100%', '250i', '10h', '10w')
---@param width string|number
function CWidget:SetWidth(width) end

--- Sets widget height (accepts pixel numbers or CSS/K2 dimension strings like '100%', '250i', '10h', '10w')
---@param height string|number
function CWidget:SetHeight(height) end

--- Sets relative X position
---@param x string|number
function CWidget:SetX(x) end

--- Sets relative Y position
---@param y string|number
function CWidget:SetY(y) end

--- Sets absolute X position
---@param x number
function CWidget:SetAbsoluteX(x) end

--- Sets absolute Y position
---@param y number
function CWidget:SetAbsoluteY(y) end

--- Returns whether widget is currently visible
---@return boolean
function CWidget:IsVisible() end

--- Sets visibility
---@param visible boolean|number
function CWidget:SetVisible(visible) end

--- Returns whether widget is enabled for interaction
---@return boolean
function CWidget:IsEnabled() end

--- Sets enabled state
---@param enabled boolean|number
function CWidget:SetEnabled(enabled) end

--- Enables widget
function CWidget:Enable() end

--- Disables widget
function CWidget:Disable() end

--- Shows widget with optional duration
---@param duration? number
function CWidget:Show(duration) end

--- Hides widget immediately
function CWidget:Hide() end

--- Fades in widget over specified milliseconds
---@param time? number
function CWidget:FadeIn(time) end

--- Fades out widget over specified milliseconds
---@param time? number
function CWidget:FadeOut(time) end

--- Schedules a delayed callback
---@param time number
---@param callback? fun()
function CWidget:Sleep(time, callback) end

--- Sets primary color tint
---@param rgba string
function CWidget:SetColor(rgba) end

--- Sets background or image texture
---@param texture string
function CWidget:SetTexture(texture) end

--- Sets font name
---@param font string
function CWidget:SetFont(font) end

--- Sets render mode
---@param mode string
function CWidget:SetRenderMode(mode) end

--- Sets selected item in combobox/listbox
---@param val string|number
---@param triggerEvent? boolean
function CWidget:SetSelectedItemByValue(val, triggerEvent) end

--- Sets web avatar image url
---@param url string
function CWidget:SetAvatar(url) end

--- Sets glow intensity
---@param val? any
function CWidget:SetGlow(val) end

--- Sets glow color
---@param color string
function CWidget:SetGlowColor(color) end

--- Sets background glow
---@param val? any
function CWidget:SetBackgroundGlow(val) end

--- Sets border glow
---@param val? any
function CWidget:SetBorderGlow(val) end

--- Sets text glow
---@param val? any
function CWidget:SetTextGlow(val) end

--- Sets drop shadow
---@param val? any
function CWidget:SetShadow(val) end

--- Sets drop shadow color
---@param color string
function CWidget:SetShadowColor(color) end

--- Sets drop shadow offset
---@param x number
---@param y number
function CWidget:SetShadowOffset(x, y) end

--- Sets outline enabled
---@param val? any
function CWidget:SetOutline(val) end

--- Sets outline color
---@param color string
function CWidget:SetOutlineColor(color) end

--- Registers input action callback ('onclick', 'onmouseover', etc.)
---@param event string
---@param fn fun(widget: CWidget)
function CWidget:SetCallback(event, fn) end

--- Clears input action callback
---@param event string
function CWidget:ClearCallback(event) end

--- Removes all child widgets
function CWidget:ClearChildren() end

--- Triggers XML onevent trigger (takes NO string arguments)
function CWidget:DoEvent() end

--- Triggers indexed XML oneventN trigger (onevent0, onevent1, etc.)
---@param index number
function CWidget:DoEventN(index) end

--- Executes an internal UI engine command
---@param cmd string
---@return string
function CWidget:UICmd(cmd) end

--- Registers a property watcher
---@param name string
---@param fn fun(widget: CWidget, ...: any)
function CWidget:RegisterWatch(name, fn) end

--- Finds a child widget by name
---@param name string
---@return CWidget?
function CWidget:GetWidget(name) end

--- Instantiates a child template widget
---@param templateName string
---@param ... any
---@return CWidget
function CWidget:Instantiate(templateName, ...) end

--- Sets 3D model path (for modelpanel widgets)
---@param modelPath string
function CWidget:SetModel(modelPath) end

--- Sets 3D visual effect path
---@param effectPath string
function CWidget:SetEffect(effectPath) end

--- Sets 3D model skin
---@param skinName string
function CWidget:SetSkin(skinName) end

--- Sets 3D skeletal animation
---@param animName string
function CWidget:SetAnim(animName) end

--- Sets 3D camera position
---@param x number
---@param y number
---@param z number
function CWidget:SetCameraPos(x, y, z) end

--- Sets 3D camera angles
---@param pitch number
---@param roll number
---@param yaw number
function CWidget:SetCameraAngles(pitch, roll, yaw) end


---@class CInterface
local CInterface = {}

--- Returns interface name
---@return string
function CInterface:GetName() end

--- Finds a widget within this interface
---@param name string
---@return CWidget?
function CInterface:GetWidget(name) end

--- Registers an interface-level property watcher
---@param name string
---@param fn fun(...)
function CInterface:RegisterWatch(name, fn) end

--- Instantiates a template in this interface
---@param templateName string
---@param ... any
---@return CWidget
function CInterface:Instantiate(templateName, ...) end

--- Executes UI command in interface context
---@param cmd string
---@return string
function CInterface:UICmd(cmd) end


---@class UIManager
---@class CUIManager
local CUIManager = {}

--- Retrieves loaded interface by name
---@param name string
---@return CInterface?
function CUIManager.GetInterface(name) end

--- Retrieves widget directly across loaded interfaces
---@param name string
---@return CWidget?
function CUIManager.GetWidget(name) end

--- Reloads specified interface XML
---@param name string
function CUIManager.ReloadInterface(name) end

--- Sets active foreground interface
---@param name string
function CUIManager.SetActiveInterface(name) end


---@class CCvarManager
local CCvarManager = {}

--- Gets cvar string value
---@param name string
---@return string
function CCvarManager.GetString(name) end

--- Gets cvar boolean value
---@param name string
---@return boolean
function CCvarManager.GetBoolean(name) end

--- Gets cvar integer value
---@param name string
---@return number
function CCvarManager.GetInteger(name) end

--- Sets cvar value
---@param name string
---@param value any
function CCvarManager.Set(name, value) end

--- Creates a new cvar
---@param name string
---@param type 'string'|'int'|'bool'|'float'
---@param defaultValue any
function CCvarManager.CreateCvar(name, type, defaultValue) end

--- Checks if cvar exists
---@param name string
---@return boolean
function CCvarManager.Find(name) end


-- ============================================================================
-- GLOBAL ENGINE FUNCTIONS & VARIABLES
-- ============================================================================

--- Global UI Manager singleton
---@type CUIManager
UIManager = UIManager or {}

--- Global Cvar management table
---@type CCvarManager
Cvar = Cvar or {}

--- Global game state table
---@type table<string, any>
Game = Game or {}

--- Returns host engine time in milliseconds. MUST be called as a function (HostTime()), never arithmetic on function.
---@return number
function HostTime() end

--- Gets cvar string
---@param name string
---@return string
function GetCvarString(name) end

--- Gets cvar boolean
---@param name string
---@return boolean
function GetCvarBool(name) end

--- Gets cvar integer
---@param name string
---@return number
function GetCvarInt(name) end

--- Sets cvar or console variable
---@param name string
---@param value any
function Set(name, value) end

--- Sets cvar
---@param name string
---@param value any
function SetCvar(name, value) end

--- Prints green message to in-game console
---@param message string
function Echo(message) end

--- Prints message to engine stdout / console.log
---@param message string
function println(message) end

--- Registers script version
---@param name string
---@param version string|number
function RegisterScript(name, version) end

--- Registers script secondary version
---@param name string
---@param version string|number
function RegisterScript2(name, version) end

--- Retrieves value from client database/config
---@param key string
---@param defaultVal? any
---@param persistent? boolean
---@param encrypted? boolean
---@param localOnly? boolean
---@return any
function GetDBEntry(key, defaultVal, persistent, encrypted, localOnly) end

--- Sets value in client database/config
---@param key string
---@param value any
---@param persistent? boolean
---@param encrypted? boolean
---@param localOnly? boolean
function SetDBEntry(key, value, persistent, encrypted, localOnly) end

--- Executes an in-game console command string
---@param cmd string
function Cmd(cmd) end

--- Plays in-game 2D audio sound
---@param soundPath string
---@param volume? number
---@param channel? number
function PlaySound(soundPath, volume, channel) end

--- Global GetWidget helper
---@param name string
---@param fromInterface? string
---@param hideErrors? boolean
---@return CWidget?
function GetWidget(name, fromInterface, hideErrors) end

--- Returns whether client is in preseason
---@return boolean
function IsPreseason() end

--- Returns whether client is using modern UI
---@return boolean
function isNewUI() end

--- Contextual interface object passed to script blocks in XML
---@type CInterface?
object = object or nil

-- Subsystem global tables
---@type table<string, any>
HoN_Store = HoN_Store or {}

---@type table<string, any>
HoN_Main = HoN_Main or {}

---@type table<string, any>
Main = Main or {}

---@type table<string, any>
Watch_System = Watch_System or {}

---@type table<string, any>
Player_Stats = Player_Stats or {}

---@type table<string, any>
Player_Stats_V2 = Player_Stats_V2 or {}

---@type table<string, any>
HoN_Matchmaking = HoN_Matchmaking or {}

---@type table<string, any>
PlayerHosted = PlayerHosted or {}

---@type table<string, any>
Match_Stats = Match_Stats or {}

---@type table<string, any>
TestSuite = TestSuite or {}
