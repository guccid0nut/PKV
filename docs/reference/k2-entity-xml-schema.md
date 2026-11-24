# K2 Engine `.entity` XML Schema & Combat Action Reference

> **Machine-Readable Companion Schema:** [`k2_entity_xml_schema.json`](./k2_entity_xml_schema.json)
> **Generator & Verifier:** [`scripts/assets/build_k2_entity_schema.py`](../../scripts/assets/build_k2_entity_schema.py) and [`scripts/verify_xml_syntax.ps1`](../../scripts/verify_xml_syntax.ps1)

This reference documents the XML entity definition schema compiled into `games/client/game/game_shared_x64.dll` (`8,043,008` bytes) and cross-verified across all **7,289** effective `.entity` files in `games/client/game/resources*.s2z` and `games/client/override/`.

---

## 1. Root Entity Definition Types

Across the 7,289 shipped `.entity` definitions, K2 registers **23** root entity element types (`IEntityDefinition` subclasses):

| Root Tag | Shipped Count | Primary Role |
| :--- | ---: | :--- |
| `<state>` | 3,094 | Timed buffs, debuffs, hidden trackers, and combat-event listeners (`oninflict`, `onframe`, `ondamaged`, `onexpired`). |
| `<ability>` | 1,161 | Active, passive, and toggle skills slotted onto heroes, pets, gadgets, neutrals, or buildings. |
| `<projectile>` | 1,047 | Linear/homing/arcing projectiles; units and gadgets can bind to projectiles for dashes, leaps, and throws. |
| `<gadget>` | 610 | Auxiliary world units (typically immobile with `GadgetImmunity`) used as timers, vision anchors, or AoE origins. |
| `<affector>` | 519 | Circular or conical AoE volumes applying periodic or single-pulse impact scripts. |
| `<item>` | 329 | Inventory-slotted tools (share `IEntityTool` combat events with `<ability>`, disabled by Perplex rather than Silence). |
| `<hero>` | 141 | Playable hero base definitions (attributes, growth, model/anim paths, ability slots, Alt Avatar `<altavatar>` blocks). |
| `<pet>` | 127 | Mobile summoned combat units controlled by players. |
| `<building>` | 82 | Structures (towers, barracks, shrines, Kongor pit props) eligible for structure target schemes and Fortification. |
| `<neutral>` | 52 | Jungle camp creeps governed by leash/respawn AI controllers. |
| `<shop>` | 29 | Shop catalogs mapping item definitions to shop tabs. |
| `<linearaffector>` | 21 | Rectangular/line AoE volumes. |
| `<statenetaccum>` | 16 | Specialized shield `<state>` subclass exposing network-accumulated shield health bars (`sethealthaccumulator`). |
| `<order>` | 16 | Scripted unit order sequences (`order.entity`). Note: `<order>` as a **root** element accepts `<on*>` children, whereas `<order>` nested inside an action script is a **leaf** `CCombatActionOrder` (`allows_children: false`). |
| `<powerup>` | 13 | River runes and pickup entities. |
| `<abilityattribute>` | 9 | Attribute bonus ability definitions. |
| `<heropet>` | 7 | Persistent hero-companion pets (e.g. Booboo). |
| `<creep>` / `<critter>` / `<capsuleaffector>` / `<game>` / `<gamelogic>` / `<waypoint>` | 16 | Lane creeps, ambient critters, capsule AoEs, and global game/mode rule scripts (`<scriptthread>`). |

---

## 2. Combat Event Handlers (`<on*>`) & Polymorphism

Because combat event registration (`COMBAT_EVENT_XML_PROCESSOR`) attaches to `IEntityDefinition`, `<on*>` event blocks work polymorphically across `<hero>`, `<ability>`, `<item>`, `<state>`, `<statenetaccum>`, `<gadget>`, `<pet>`, `<projectile>`, `<affector>`, `<building>`, and `<neutral>`.

### Default Attributes on Every `<on*>` Combat Event

| Attribute | Type | Default | Behavior |
| :--- | :--- | :--- | :--- |
| `priority` | `int` | `0` | Execution order when multiple scripts listen to the same combat event. |
| `propagatetoillusions` | `bool` | `true` | **Critical:** Combat event blocks propagate to illusions by default unless `propagatetoillusions="false"` is explicitly set. |
| `activateonbounces` | `bool` | `false` | Whether projectile/attack bounce impacts trigger the script. |
| `responsetype` | `string` | `""` | Response classification (`Game.LookupResponseType`). |
| `ignoreresponsetype` | `string` | `""` | Suppresses execution when the incoming event carries a matching response flag (e.g. `NoResponse`). |

### Core Lifecycle & Combat Events (`verified` in `game_shared_x64.dll`)

- **Frame & Timer:** `onframe`, `onframeimpact`, `oninterval`, `onthink`, `ontimer`, `ontimer0expire`..`ontimer3expire`
- **Cast & Activation:** `onactivatestart`, `onactivateprecost`, `onactivatepreimpact`, `onactivateimpact`, `onactivateend`, `ondoubleactivate`, `ontoggleon`, `ontoggleoff`, `checkcost`, `checktriggeredcost`, `activatecost`
- **Channeling:** `onchannelstart`, `onchannelframe`, `onchannelbreak`, `onchannelend`, `onchannelingstart`, `onchannelingframe`, `onchannelingbreak`, `onchannelingend`
- **Impact & Damage Pipeline:** `onprecost`, `onaction`, `onpreimpact`, `onpredamage`, `ondamageevent`, `onimpact`, `onimpactinvalid`, `ondamage`, `ondamaged`, `onheal`, `onhealed`, `onchangehealth`, `onmanareduction`, `onevade`, `onevaded`
- **Attack Pipeline (Attacker vs. Victim):**
  - Self attack tool: `onattackstart`, `onattack`, `onattackpreimpact`, `onattackpredamage`, `onattackdamageevent`, `onattackimpact`, `onattackimpactinvalid`, `onattackend`
  - Owner attacking: `onattackingstart`, `onattackingpreimpact`, `onattackingpredamage`, `onattackingdamageevent`, `onattackingpostimpact`
  - Owner attacked: `onattackedstart`, `onattackedpreimpact`, `onattackedpredamage`, `onattackeddamageevent`, `onattackedpostimpact`
- **State & Unit Lifecycle:** `oncreate`, `onspawn`, `onrespawn`, `onfailedrespawn`, `onownerrespawn`, `oninflict`, `oninflicted`, `onrefresh`, `onexpired`, `ondeath`, `onunkillabledeath`, `onkill`, `onindirectkill`, `onkilled`, `onassist`, `onstunned`, `onsmackdowned`, `onlevelup`, `onlearn`, `onupgrade`
- **Items & Physics:** `onprepurchase`, `onpurchase`, `onpickup`, `ondropped`, `onsold`, `ontouch`, `ontouched`, `oncollide`, `ondisjointed`, `onorderdisjointed`, `onleash`, `onpremove`

---

## 3. Leaf vs. Branch Combat Actions (Critical Silent-Bug Trap)

Every action tag inside an `<on*>` script is compiled with one of two C++ processor macros in `game_shared_x64.dll`:

1. **Branch Actions (`BEGIN_COMBAT_ACTION_BRANCH_XML_PROCESSOR` / `END_XML_PROCESSOR(pAction->GetActionScript())`):**
   Accept nested child actions and execute them conditionally or iteratively:
   - Control flow & conditionals: `<condition>`, `<elsecondition>`, `<else>`, `<chance>`, `<compare>`, `<comparestrings>`, `<comparecvar>`, `<comparetiledefinition>`, `<scriptcondition>`, `<while>`, `<whilescriptcondition>`, `<loop>`
   - Spatial & collection iterators: `<areaofeffect>`, `<lineofeffect>`, `<foreachplayer>`, `<foreachproxy>`, `<foreachitem>`, `<foreachstate>`, `<combatevent>`
   - Predicates & guards: `<cantarget>`, `<canactivate>`, `<canattack>`, `<caneffect>`, `<isready>`, `<isstealthed>`, `<isterminated>`, `<isitem>`, `<isability>`, `<issplitprojectile>`, `<isiteminbackpack>`, `<isiteminstash>`, `<hasmodifier>`, `<hasavatarkey>`, `<hasmodel>`, `<ownsproduct>`, `<entitytype>`, `<targettype>`, `<combatsupertype>`, `<combateffecttype>`, `<damagesupertype>`, `<damageeffecttype>`, `<currentdamagesupertype>`, `<currentdamageeffecttype>`, `<casteffecttype>`, `<upgradeselected>`, `<consume>`, `<checkcamerapos>`
   - Attack hook injectors: `<addattackpreimpactactions>`, `<addattackpredamageactions>`, `<addattackdamageeventactions>`, `<addattackimpactactions>`, `<addattackimpactinvalidactions>`

2. **Leaf Actions (`BEGIN_COMBAT_ACTION_LEAF_XML_PROCESSOR` -> `END_XML_PROCESSOR_NO_CHILDREN`):**
   **320** verified combat actions (`<damage>`, `<applystate>`, `<heal>`, `<spawnunit>`, `<spawnprojectile>`, `<spawnaffector>`, `<playeffect>`, `<setvar0>`, `<Popup>`, `<printdebuginfo>`, `<printvalue>`, etc.) terminate with `END_XML_PROCESSOR_NO_CHILDREN`.
   - **Engine Footgun:** K2 does **not** crash when you place child tags inside a leaf action—it **silently discards** all child elements!
   - **Automated Protection:** `scripts/verify_xml_syntax.ps1` and `scripts/gate_check.ps1` Tier 3 block any non-root leaf tag containing child elements (`[K2-ENTITY-LEAF]`).

All `BEGIN_COMBAT_ACTION_*` nodes default `source="source_entity"` and `target="target_entity"` unless overridden on the element.

---

## 4. `<aura>` Override-by-Name inside `<modifier>`

In `CXMLProcessor_aura` (`game_shared_x64.dll`), K2 executes `pObject->RemoveAura(sName)` **before** registering the new `CAuraDefinition` (`sStateName` or `sGadgetName` must be non-empty, and `propagatetoillusions` defaults to `true`):

```xml
<ability name="Ability_ExampleAura" ...>
    <aura name="example_aura" state="State_ExampleAura_Base" radius="900" targetscheme="ally_units" />
    <modifier key="ult_boosted" modpriority="100">
        <!-- Same name="example_aura" cleanly replaces the base aura instead of stacking two auras -->
        <aura name="example_aura" state="State_ExampleAura_Boosted" radius="1200" targetscheme="ally_units" />
    </modifier>
</ability>
```

---

## 5. Dynamic Properties (`source_*`, `target_*`, `this_*`)

Action attributes such as `a="..."`, `b="..."`, and `test="..."` accept runtime **dynamic properties** resolved against entity pointers (`source_`, `target_`, `this_`, `this_owner_`, `proxy_`, `inflictor_`, `stack_`):

- **Current vs. Base Attributes:**
  - `target_intelligence` / `source_agility` / `source_strength`: Total current attribute including items, buffs, and debuffs.
  - `target_baseintelligence` / `source_baseagility` / `source_basestrength`: Starting attribute **plus natural level-up stat growth**, excluding external item/buff modifiers.
  - **Unexposed Property:** Per-level stat growth (`intelligenceperlevel` / `target_intelligenceperlevel`) is **not** exposed to XML action scripts.
- **Entity Level:** Use `target_level` or `source_level` (not `target_herolevel`).
- **Canonical Shipped Reference:** Inspect `heroes/chi/ability_03/ability.entity` inside `games/client/game/resources1.s2z` (via `scripts/vfs/read_archive_entry.ps1`) for complex multi-property dynamic arithmetic (`setvar0` + `op` chains).

---

## 6. Built-In `.entity` Debug & Visual Telemetry Tags

| XML Leaf Tag | C++ Processor (`game_shared_x64.dll`) | Attributes & Defaults | Purpose |
| :--- | :--- | :--- | :--- |
| `<printdebuginfo />` | `CCombatActionPrintDebugInfo` | *(none)* | Dumps active `source_entity`, `target_entity`, `inflictor_entity`, `proxy_entity`, `var0..var3`, `accumulator`, and `result` to `console.log`. |
| `<printvalue value="result" />` | `CCombatActionPrintValue` | `value`, `label` (defaults to `value`) | Logs a named dynamic property or stack variable (`result`, `var0`, `target_health`, `source_mana`) to `console.log`. |
| `<printpos value="target_position" />` | `CCombatActionPrintPos` | `value`, `label` (defaults to `value`) | Logs 3D world `(x, y, z)` coordinates of a position token. |
| `<printpersistentmodifierkeys />` | `CCombatActionPrintPersistentModifierKeys` | `entity="this_entity"` | Prints all active persistent modifier keys on an entity. |
| `<print text="..." />` | `CCombatActionPrint` | `text` | Prints a literal trace message when an event branch fires. |
| `<drawdebugline start="source_position" end="target_position" />` | `CCombatActionDrawDebugLine` | `start`, `end`, `arrow="false"`, `color="blue"` | Renders a 3D debug line/arrow in-world (enable `Set cg_drawDebugLines true` in console). |
| `<drawdebugx pos="target_position" />` | `CCombatActionDrawDebugX` | `pos`, `color="blue"` | Renders a 3D world-space `X` marker at `pos` (`Set cg_drawDebugLines true`). |

---

## 7. Forum Drift Ledger: 25 Tags Absent from Our Build (`absent_in_build`)

Cross-referencing archived 2020–2021 Frostburn forum documentation (`topic/1546`) against `games/client/game/game_shared_x64.dll` and all 7,289 shipped `.entity` files identified **25** later-patch tags that are **not compiled** into our K2 build (0 binary UTF-16 hits, 0 corpus occurrences). `scripts/verify_xml_syntax.ps1` and `scripts/gate_check.ps1` Tier 3 automatically reject these tags (`[K2-ENTITY-ABSENT-TAG]`):

- **Absent Subscriber System:** `onsubscriberitemacquisition`, `onsubscriberunitdeath`, `onsubscriberunitkill`, `subscribeitemacquisition`, `subscribeunitdeath`, `subscribeunitkill`
- **Absent Day/Night Script Controls:** `getlengthofday`, `gettimeofdawn`, `pausedaynightcycle`, `setlengthofday`, `settimeofdawn`, `toggledaynight`
- **Absent Affector Radius Getters/Setters:** `getaffectorinnerradius`, `getaffectorradius`, `setaffectorinnerradius`, `setaffectorradius`
- **Absent Item-Steal & Misc Callbacks:** `markheroattack`, `oncourierdelivered`, `onprestoleitem`, `onremove`, `onstoleitem`, `onunitexpired`, `setcvar` (combat action), `setstealvalue`, `settooltipnumparam`
