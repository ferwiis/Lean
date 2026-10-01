# Auroboros — Handoff global antes del SDD final del Binary Pipeline

**Estado:** antesala del siguiente SDD de implementación  
**Objetivo:** poder abrir un chat nuevo y recuperar rápidamente el contexto técnico y las decisiones pendientes sin depender de releer toda la conversación.  
**Regla de trabajo:** **no enviar todavía estos puntos a Codex como “impleméntalos todos”.** Primero se deben debatir uno por uno con el usuario, fijar decisiones finales y solo después redactar un SDD ejecutable, preciso y cerrado.

---

## 1. Objetivo general del proyecto en esta etapa

Auroboros usa un fork privado de QuantConnect LEAN como motor de backtesting/trading. En esta fase se está trabajando exclusivamente en hacer que el pipeline histórico binario personalizado sea:

- correcto semánticamente;
- streaming y de memoria acotada;
- compatible con el ciclo de vida nativo de LEAN;
- mantenible frente a cambios de upstream;
- verificable con datos reales;
- suficientemente sólido antes de abordar multiprocessing.

La prioridad actual **no es multiprocessing**. Primero debe quedar perfecto el comportamiento de un proceso individual leyendo historia binaria.

---

## 2. Repositorios y arquitectura Git

Repositorios principales:

- `ferwiis/Auroboros`
- `ferwiis/Lean`

Ramas de trabajo relevantes:

- Auroboros: `dev`
- LEAN fork: `auroboros`
- LEAN fork `master`: debe mantenerse lo más cercano posible a `QuantConnect/Lean master`

Arquitectura deseada:

```text
QuantConnect/Lean upstream/master
            │
            ▼
      ferwiis/Lean master
            │
            ▼
    ferwiis/Lean auroboros
      + cambios Auroboros
```

La intención final es conservar un patch stack pequeño y limpio sobre upstream. Los commits temporales son útiles mientras se audita el trabajo; después de validar todo, podrán hacerse `fixup/squash`.

**Importante:** el usuario acaba de ajustar manualmente el contrato `spread → reserved` en Data Engineering. En un chat nuevo se debe verificar si esos cambios ya fueron commit/push antes de asumir que GitHub refleja el estado local.

---

## 3. Qué se cambió ya en LEAN

El pipeline anterior utilizaba `MemoryMappedFile`, pero cargaba y materializaba grandes rangos históricos en listas antes de entregarlos a LEAN.

La migración realizada por Codex cambió la arquitectura a streaming:

```text
archivo .bin
    ↓
MemoryMappedFile
    ↓
reader forward-only
    ↓
factory binaria
    ↓
wrappers/lifecycle nativos de LEAN
    ↓
History / DataFeed
```

Cambios conceptuales principales ya realizados:

- eliminación del viejo `BinaryDataLoader`;
- readers `TradeBar` y `QuoteBar` forward-only;
- `BinarySubscriptionEnumeratorFactory`;
- `BinaryDataFeed` apoyado en `FileSystemDataFeed`;
- `BinaryHistoryProvider` apoyado en `SubscriptionDataReaderHistoryProvider`;
- seams pequeños en clases nativas para sustituir únicamente el origen físico;
- consolidación streaming;
- resolver de archivos separado de la lectura;
- tests unitarios, integración con datos reales y pruebas de runtime realizadas previamente por Codex.

La mejora principal **no es que MMF sea nuevo**: el sistema anterior ya utilizaba MMF. La mejora es que se dejó de materializar la historia completa antes de consumirla.

---

## 4. Resultado arquitectónico actual

Antes:

```text
.bin/MMF
→ leer muchos registros
→ crear todos los objetos
→ List<>
→ deduplicar/ordenar
→ consolidar
→ LEAN empieza a consumir
```

Ahora:

```text
.bin/MMF
→ leer un registro
→ validar
→ proyectar a BaseData
→ consolidar si hace falta
→ entregar
→ siguiente registro
```

El objetivo estructural alcanzado es que la memoria retenida por el source reader deje de crecer con la longitud total de la historia.

No se debe afirmar todavía un multiplicador numérico de rendimiento frente al loader antiguo porque no existe un benchmark controlado viejo-vs-nuevo equivalente.

---

## 5. Validación empírica del archivo Forex real

Se inspeccionó un `.bin` real EURCAD 5M junto con su CSV equivalente.

Resultado:

- el `.bin` contiene registros de **64 bytes**;
- la cantidad de registros coincide exactamente con las filas del CSV;
- se reconstruyó un CSV leyendo únicamente el binario;
- el CSV reconstruido resultó byte-por-byte idéntico al CSV original;
- por tanto el orden y los valores principales de Data Engineering quedaron validados empíricamente para ese dataset.

Layout físico confirmado del registro Forex actual:

```text
double timestamp

float open
float high
float low
float close
float volume

float bid_open
float bid_high
float bid_low
float bid_close

float ask_open
float ask_high
float ask_low
float ask_close

4 bytes finales reservados
```

Total:

```text
64 bytes
```

---

## 6. Problema `spread / padding` — RESUELTO

Originalmente:

- Python declaraba 60 bytes de campos explícitos y `ctypes.Structure` terminaba en 64 bytes por alineación.
- El header C tenía un `float spread` en los bytes 60..63.
- Por casualidad ABI, el padding final de Python ocupaba exactamente la misma posición que el supuesto `spread` de C.
- Los datos reales mostraron que esos cuatro bytes estaban en cero en todos los registros inspeccionados.

La decisión ya tomada es convertir esa coincidencia accidental en contrato explícito:

```text
bytes 60..63 = reserved
```

Conceptualmente:

```text
Python: uint32 reserved
C:      uint32_t reserved
C#:     uint Reserved
```

El tamaño permanece en 64 bytes. No hay que reconvertir los históricos.

`reserved` no es un identificador de registro. La navegación se obtiene porque todos los registros tienen tamaño fijo:

```text
offset = recordIndex * 64
```

El timestamp identifica semánticamente el momento del registro.

Este problema debe considerarse **cerrado** en el próximo SDD.

---

## 7. Qué representa realmente el dataset Forex actual

El pipeline de Data Engineering obtiene primero agregados OHLCV de Polygon:

```text
timestamp + OHLCV
```

Cuando se genera QuoteBar también obtiene quotes Bid/Ask y construye:

```text
OHLCV global
+
Bid OHLC
+
Ask OHLC
```

Por tanto el archivo Forex de 64 bytes es físicamente un **superset** de un TradeBar OHLCV simple.

Esto significa que los históricos actuales ya contienen:

```text
información necesaria para QuoteBar
+
información suficiente para una posible proyección TradeBar
```

No existe ninguna razón identificada hasta ahora para volver a pagar Polygon ni volver a descargar 2010–2025 para resolver el pipeline actual.

---

# 8. Problemas todavía abiertos antes del SDD final

Los siguientes puntos deben debatirse **uno por uno**. El objetivo es que ninguna decisión arquitectónica importante quede en manos de Codex por inferencia.

---

## Problema 1 — Formato físico y tipo lógico LEAN están acoplados incorrectamente

Actualmente la factory tiende a asumir:

```text
LEAN pide QuoteBar
→ reader físico 64 bytes

LEAN pide TradeBar
→ reader físico 32 bytes
```

Pero los archivos Forex existentes son físicamente registros enriquecidos de 64 bytes.

### Ejemplo del fallo

Un archivo de 64 bytes también satisface:

```text
fileLength % 32 == 0
```

Por tanto un reader TradeBar32 podría pensar que el archivo es válido.

Entonces:

```text
registro físico real de 64 bytes
┌─────────────────────────┬─────────────────────────┐
│ primeros 32 bytes       │ segundos 32 bytes      │
└─────────────────────────┴─────────────────────────┘
```

El reader de 32 bytes podría interpretar la segunda mitad —Bid/Ask— como si fuera un segundo registro con nuevo timestamp y OHLCV.

### Decisión pendiente

Separar explícitamente:

```text
PhysicalRecordType
```

de:

```text
RequestedLeanType
```

---

## Problema 2 — El archivo no identifica su schema físico

Un filename actual como:

```text
1-FX_EURCAD_5M_20091026-20100417.bin
```

informa:

- índice;
- mercado;
- símbolo;
- timeframe;
- rango temporal.

Pero no informa:

- `TradeBarRow32`;
- `FxQuoteRow64`;
- versión del ABI.

Hoy nosotros conocemos el origen del dataset, pero un reader robusto no debería depender de conocimiento humano implícito.

### Decisión pendiente

Elegir cómo identificar el schema:

- manifest externo;
- metadata;
- filename versionado;
- header binario;
- regla contractual específica para el dataset actual.

No implementar todavía hasta decidirlo.

---

## Problema 3 — Definir formalmente las proyecciones lógicas

El mismo registro Forex de 64 bytes contiene dos familias de información:

```text
FxQuoteRecord64
       │
       ├── Bid + Ask ──→ QuoteBar
       │
       └── OHLCV ──────→ TradeBar
```

### Decisiones pendientes

Definir:

- si se soportará formalmente la proyección a `TradeBar`;
- en qué circunstancias;
- si aplica exclusivamente al dataset Forex existente;
- cómo se comporta History si se solicita otro tipo lógico.

Codex no debe inventar esta política.

---

## Problema 4 — Semántica potencialmente inconsistente de `QuoteBar.Value`

El reader actual coloca el `record.Close` global como `QuoteBar.Value`.

Pero en LEAN `QuoteBar.Close` se deriva del Bid y Ask, normalmente mediante midpoint cuando ambos existen.

### Ejemplo

```text
aggregate close = 1.10000
Bid.Close       = 1.09980
Ask.Close       = 1.10040

QuoteBar.Value actual = 1.10000
QuoteBar.Close LEAN   = 1.10010
```

Así dos consumidores podrían observar precios diferentes para la misma barra.

### Decisión pendiente

Definir si:

```text
QuoteBar.Value = QuoteBar.Close derivado de Bid/Ask
```

y conservar el aggregate OHLCV solamente como información física disponible para otras proyecciones.

---

## Problema 5 — History de Auroboros usa un overload problemático para Forex

En `OrderManager` existe una llamada conceptual de este tipo:

```csharp
History(symbol, bars, Resolution.Minute)
```

Ese overload trabaja con `TradeBar`.

LEAN actual indica explícitamente que para Forex/CFD la historia debe solicitarse como `QuoteBar`.

### Ejemplo posible

```text
Adaptive Stop Loss
→ pide History TradeBar sobre Forex
→ no obtiene la historia esperada
→ NoHistory
→ fallback
```

aunque existan datos históricos.

### Trabajo pendiente

- revisar esa llamada;
- buscar otras llamadas equivalentes en Auroboros;
- definir cuál debe ser la API correcta para Forex;
- agregar test que pruebe que la estrategia obtiene historia real y no cae accidentalmente al fallback.

---

## Problema 6 — Selección del timeframe nativo potencialmente ineficiente

Actualmente, cuando varias resoluciones pueden consolidarse a la solicitada, se elige la compatible más pequeña.

### Ejemplo

```text
datos disponibles: 5M, 15M
request LEAN:      30M
```

Actual:

```text
5M → 30M
```

requiere seis registros por barra final.

Alternativa:

```text
15M → 30M
```

requiere dos.

### Decisión pendiente

Definir si la política debe ser:

```text
mayor periodo nativo que divide exactamente al solicitado
```

o si existe una razón de fidelidad para preferir siempre la fuente más fina.

---

## Problema 7 — Request más fino que el dataset disponible

Ejemplo:

```text
dataset nativo = 5M
request LEAN   = 1M
```

El sistema actual puede devolver 5M aunque la configuración/request continúe describiendo 1M.

Esto puede tener consecuencias con:

- fill-forward;
- indicadores;
- consolidadores;
- semántica de `Period`;
- expectations del algoritmo.

### Decisión pendiente

Elegir política explícita:

- rechazar;
- normalizar;
- soportar de otra manera.

La opción debe decidirse antes de Codex.

---

## Problema 8 — Deduplicación first-file-wins puede perder gaps complementarios

El reader streaming descarta timestamps menores o iguales al último emitido.

Eso funciona bien si dos archivos solapados contienen los mismos datos.

### Caso problemático

```text
Archivo A:
00:00  00:05        00:15

Archivo B:
       00:05  00:10 00:15
```

Después de terminar A en `00:15`, al recorrer B el `00:10` puede descartarse por ser anterior al último timestamp emitido.

### Decisión pendiente

Determinar primero si Data Engineering garantiza que los overlaps son duplicados completos.

Si sí:

- documentar;
- caracterizar con tests.

Si no:

- diseñar merge streaming con lookahead acotado.

---

## Problema 9 — Fallback sintético Bid/Ask debe convertirse en política explícita

Data Engineering actualmente tiene fallback cuando faltan quotes.

Conceptualmente:

```text
aggregate close = 1.25000
no quote válido

Bid.Close = 1.25000
Ask.Close = 1.25010
```

por un `min_spread`.

Esto permitió evitar barras sin Bid/Ask, pero LEAN puede utilizar esos valores para:

- fills;
- `Security.BidPrice`;
- `Security.AskPrice`;
- cálculo de spread;
- lógica de Auroboros.

### Decisión pendiente

Definir si ese fallback:

- es contrato oficial;
- debe conservarse;
- debe marcarse;
- simplemente debe caracterizarse;
- o debe tratarse de otra forma.

No asumir que es automáticamente incorrecto.

---

## Problema 10 — Los readers recorren desde el registro cero hasta `startUtc`

El streaming ya mantiene memoria acotada, pero una petición tardía dentro de un archivo puede obligar a recorrer todos los timestamps anteriores.

### Ejemplo

Archivo:

```text
6 meses de datos 5M
```

History:

```text
últimos 2 días
```

El reader puede inspeccionar todas las barras anteriores solo para descartarlas.

### Mejora futura

Los registros son:

- de tamaño fijo;
- ordenados por timestamp.

Por tanto se puede usar:

- binary search;
- seek directo;
- índice temporal.

Esto es **optimización**, no blocker de corrección. Debe medirse antes de implementarlo.

---

## Problema 11 — Faltan pruebas explícitas de lifecycles delicados

Aunque ya existe buena cobertura, conviene cerrar específicamente:

- warmup → normal;
- `LastPointTracker`;
- fill-forward;
- DST crossover;
- History Forex QuoteBar;
- no duplicados/huecos al cruzar estados del motor.

### Riesgo

Un reader aislado puede pasar todos sus tests y aun así existir un fallo en el lifecycle completo de LEAN.

La siguiente suite debe probar comportamiento end-to-end del backend binario.

---

## Problema 12 — TradeBar32 no tiene archivo histórico real para validación empírica

El contrato estructural actual es:

```text
double timestamp
float OHLCV
+ alineación
= 32 bytes
```

Esto coincide entre Python/C y con la corrección realizada en LEAN.

Sin embargo no existe actualmente un `.bin` TradeBar histórico real equivalente al QuoteBar EURCAD usado para la validación byte-a-byte.

### Decisión recomendada a discutir

- mantener `TradeBarRow32` como schema válido para datasets futuros;
- no fingir que los históricos Forex actuales son TradeBar32;
- si se necesita TradeBar sobre los históricos existentes, proyectarlo desde FxQuote64.

No se necesita volver a descargar data para resolver esto.

---

## Problema 13 — Mercado soportado y schema soportado están demasiado mezclados

Actualmente el backend binario está orientado explícitamente a Forex y a tipos TradeBar/QuoteBar.

Eso está bien como scope actual, pero no se debe generalizar incorrectamente a otros mercados.

### Importante

Para Forex:

```text
quotes son fundamentales
```

Para acciones/opciones/futuros pueden existir:

```text
trade aggregates
≠
quote aggregates
```

como fuentes distintas.

### Decisión pendiente

Diseñar extensibilidad futura sin afirmar soporte que todavía no existe.

El próximo commit puede continuar siendo Forex-only si queda documentado como scope intencional.

---

## Problema 14 — Falta benchmark controlado viejo-vs-nuevo

Codex ya realizó pruebas de runtime con resultados buenos y el nuevo diseño es estructuralmente mejor en memoria retenida.

Pero no existe aún un benchmark idéntico:

```text
mismo dataset
mismo rango
mismo hardware
mismo workload

OLD BinaryDataLoader
vs
NEW streaming pipeline
```

### Métricas deseables

- tiempo total;
- time-to-first-bar;
- managed heap;
- working set/private bytes;
- GC Gen0/1/2;
- allocations.

### Consecuencia

Se puede afirmar la mejora arquitectónica.

No se debe afirmar todavía:

```text
“5x”
“6x”
“7x”
```

sin esa comparación.

---

## Problema 15 — Limpieza final del patch stack y documentación temporal

Mientras se audita:

```text
commit base
+
commit temporal
+
próximo commit temporal
```

es útil porque permite revisar diffs independientes.

Cuando la implementación esté totalmente validada:

1. sincronizar `master` con upstream;
2. rebase de `auroboros`;
3. resolver cualquier conflicto;
4. ejecutar suite crítica;
5. retirar documentación temporal que no deba formar parte del engine;
6. hacer `fixup/squash`;
7. dejar un patch custom limpio sobre `master`.

No hacer el squash prematuramente: primero preservar auditabilidad.

---

# 9. Decisiones que ChatGPT y el usuario deben debatir antes de escribir el SDD

Estas decisiones **no deben delegarse a Codex**:

1. **Cómo identificar el physical schema.**
2. **Cómo separar physical record de requested LEAN type.**
3. **Qué proyecciones soporta FxQuote64.**
4. **Cuál es la semántica canónica de `QuoteBar.Value`.**
5. **Qué hacer cuando el request es más fino que el dataset.**
6. **Cuál native timeframe elegir cuando existen varias opciones compatibles.**
7. **Qué garantía existe sobre overlaps entre archivos.**
8. **Cuál es la política oficial del fallback Bid/Ask sintético.**
9. **Qué mercados se consideran soportados ahora y cuáles quedan como extensión futura.**

Cada punto debe terminar en una decisión explícita del tipo:

```text
DECISIÓN:
...

RAZÓN:
...

COMPORTAMIENTO ESPERADO:
...

CASO QUE DEBE FUNCIONAR:
...

CASO QUE DEBE FALLAR:
...
```

Esas decisiones serán después incorporadas al SDD final.

---

# 10. Orden recomendado para debatir los problemas

Prioridad conceptual:

```text
1. Physical schema vs logical type
2. Identificación/versionado del schema
3. Proyección FxQuote64 → QuoteBar / TradeBar
4. QuoteBar.Value
5. History correcto para Forex
6. Request finer-than-native
7. Native timeframe selection
8. Overlap/deduplicación
9. Fallback Bid/Ask
10. Lifecycle tests
11. TradeBar32 future scope
12. Multi-market extensibility
13. Benchmark
14. Seek/index optimization
15. Cleanup/squash final
```

Primero corrección semántica. Después optimización.

---

# 11. Qué NO hacer todavía

No implementar todavía:

- shared memory;
- IPC;
- data service central;
- caché cross-process;
- rediseño de multiprocessing;
- optimizaciones complejas para muchas familias/procesos.

Recordatorio:

```text
🟡 PRIMERO NECESITAMOS QUE LA LECTURA POR PROCESO SEA CORRECTA,
   ÓPTIMA Y ESTABLE.

🟡 MULTIPROCESSING VIENE DESPUÉS.
```

Cuando el pipeline individual esté cerrado, entonces se perfilarán:

```text
1 / 2 / 4 / 8 / 16 procesos
```

midiendo CPU, memoria privada, working set/PSS, GC, page faults, disco y elapsed time.

---

# 12. Cómo debe ser el SDD final para Codex

El próximo SDD no debe decir:

```text
“analiza estos problemas y decide la mejor solución”
```

Debe llegar a Codex con las decisiones ya tomadas.

Formato deseado:

```text
CURRENT STATE
SUPPORTED SCOPE
BINARY ABI
INVARIANTS
EXACT SEMANTICS
FILES ALLOWED TO CHANGE
FILES NOT TO CHANGE
IMPLEMENTATION REQUIREMENTS
ERROR POLICY
TEST MATRIX
REAL-DATA CHARACTERIZATION
PERFORMANCE ACCEPTANCE
BUILD/TEST COMMANDS
NON-GOALS
COMPLETION CHECKLIST
```

Objetivo:

> Codex debe ejecutar una especificación, no diseñar la semántica sobre la marcha.

La intención de este proceso es hacer las iteraciones de arquitectura **entre el usuario y ChatGPT**, y entregar a Codex una sola especificación suficientemente completa como para minimizar iteraciones posteriores.

---

# 13. Estado de cierre de esta sesión

### Resuelto

- migración conceptual del loader batch a streaming;
- integración mediante lifecycles nativos de LEAN;
- validación de QuoteBar real;
- layout real de 64 bytes;
- `spread/padding` entendido;
- contrato final `reserved` decidido y ajustado;
- históricos existentes considerados compatibles;
- necesidad de Bid/Ask para Forex entendida;
- no se necesita redescargar los históricos para el problema actual.

### Pendiente

Los 15 problemas de la sección 8, con especial prioridad en las decisiones semánticas de la sección 9.

### Próximo paso al retomar

No empezar programando.

Comenzar con:

> **Problema 1 — separación entre physical record y logical LEAN type.**

Debatirlo hasta llegar a una decisión explícita. Después registrar la decisión y avanzar al Problema 2.

---

## Nota para un chat nuevo

Usar este documento como fuente de recontextualización.

Antes de proponer cambios:

1. confirmar el estado actual de `Auroboros/dev`;
2. confirmar el estado actual de `Lean/auroboros`;
3. verificar si el ajuste `reserved` ya fue push/commit;
4. no asumir que el repo remoto contiene cambios locales recientes;
5. no comenzar implementación;
6. continuar el debate arquitectónico problema por problema;
7. conservar como meta final un SDD único y altamente prescriptivo para Codex.
