# Auroboros LEAN — Binary Pipeline
## Problemas y mejoras pendientes para la siguiente iteración

### Propósito

Este documento reúne los problemas, ambigüedades, riesgos y mejoras detectados
después de auditar la migración del pipeline binario de Auroboros desde una
arquitectura batch basada en `BinaryDataLoader` hacia una arquitectura
forward-only/streaming integrada con el lifecycle nativo de LEAN.

El objetivo de la siguiente iteración NO es rediseñar nuevamente el pipeline.
La arquitectura streaming actual debe considerarse la base.

La siguiente fase debe endurecerla, cerrar comportamientos ambiguos,
incrementar la cobertura de validación y eliminar riesgos antes de integrar
los commits temporales dentro del commit base de Auroboros.

## Problemas pendientes

1. **Política incorrecta o subóptima al seleccionar el timeframe nativo para consolidación**

   Actualmente, cuando existen varios períodos nativos capaces de producir el
   período solicitado, `BinaryFileResolver.SelectNativePeriod()` selecciona el
   período más pequeño compatible.

   Ejemplo:

   `5M + 15M disponibles → request 30M → actualmente selecciona 5M`

   Sin embargo, `15M → 30M` requiere leer y construir muchos menos objetos que
   `5M → 30M`. Debe definirse explícitamente si la prioridad correcta es máxima
   fidelidad de fuente o mínima cantidad de registros. Para workloads masivos de
   backtesting, probablemente interesa elegir el período compatible más cercano
   al target, siempre que no cambie la semántica de los datos.

2. **Comportamiento ambiguo cuando el request es más fino que el dataset disponible**

   Actualmente, si LEAN solicita por ejemplo `1M` pero el dataset binario más
   fino disponible es `5M`, la factory entrega barras `5M` en lugar de fabricar
   datos `1M`.

   La decisión de no inventar granularidad es correcta, pero queda una posible
   inconsistencia: el `SubscriptionRequest` y la configuración de LEAN siguen
   describiendo `1M`, mientras la fuente real emite `5M`. Esto puede interactuar
   de forma inesperada con fill-forward, filtros y otros wrappers. Debe decidirse
   si este caso debe rechazarse explícitamente, adaptar la configuración o
   soportarse formalmente con tests que demuestren una semántica correcta.

3. **Falta una validación runtime con un archivo TradeBar binario real**

   El ABI de TradeBar fue inspeccionado y corregido a registros de 32 bytes,
   incluyendo el padding nativo del productor, y existen tests sintéticos que
   generan datos con ese formato.

   Sin embargo, no se dispuso de un `.bin` TradeBar real producido por el pipeline
   de Auroboros. Antes de declarar definitivamente estable el contrato, debe
   ejecutarse al menos una prueba end-to-end contra un archivo real proveniente
   del productor original.

4. **Discrepancia entre el contrato QuoteBar del productor Python y el header C**

   El formato validado para QuoteBar ocupa 64 bytes e incluye el campo `spread`.
   El header/productor C y el binding Python no parecen describir este campo de
   exactamente la misma manera, aunque la alineación actual siga produciendo
   registros de 64 bytes.

   Esta coincidencia de tamaño no debería utilizarse como sustituto de un contrato
   formal. Debe reconciliarse el layout entre productor C, bindings Python y
   reader C# y documentarse explícitamente el ABI final de QuoteBar.

5. **La estrategia first-file-wins puede perder datos en overlaps no idénticos**

   Los readers eliminan timestamps repetidos de manera streaming utilizando el
   último timestamp emitido. Esto funciona correctamente cuando los archivos
   solapados contienen datos duplicados o supersets coherentes.

   Sin embargo, si un archivo anterior contiene un hueco y un archivo posterior
   solapado contiene el timestamp faltante, ese punto podría descartarse por ser
   anterior al último timestamp ya emitido. Debe definirse si los datasets de
   Auroboros garantizan overlaps idénticos. Si no existe esa garantía, habrá que
   implementar un merge streaming con lookahead limitado en lugar de simple
   first-file-wins.

6. **Los requests que empiezan profundamente dentro de un archivo escanean desde el registro cero**

   Los readers son streaming y ya no materializan el dataset completo, pero al
   abrir cada `.bin` comienzan desde el primer registro y descartan uno por uno
   hasta alcanzar `startUtc`.

   Para archivos grandes y requests pequeños situados cerca del final, esto puede
   desperdiciar CPU. Al tener registros de tamaño fijo y timestamps ordenados,
   puede añadirse posteriormente binary search, índice temporal o seek directo.
   No es un problema de corrección ni debe rediseñar el pipeline; es una
   optimización localizada que debe justificarse con profiling.

7. **Falta cobertura específica para cambios DST y conversiones de timezone**

   Existen tests de UTC → exchange timezone y el comportamiento básico quedó
   validado, pero falta cubrir explícitamente una ventana que atraviese un cambio
   de horario de verano/invierno.

   Dado que History, filtros, strict daily end times y fill-forward trabajan con
   tiempos locales de exchange, conviene demostrar que el pipeline binario no
   introduce duplicados, huecos o offsets incorrectos durante transiciones DST.

8. **Falta un test end-to-end específico del BinaryDataFeed durante warmup**

   La nueva arquitectura hereda correctamente de `FileSystemDataFeed` y reutiliza
   `LastPointTracker`, warmup, fill-forward, filtros y schedules nativos. También
   pasaron regresiones relevantes de LEAN y backtests reales.

   Aun así, debe existir al menos un test propio del backend binario que atraviese
   explícitamente la transición `warmup → normal data` y verifique ausencia de
   gaps, duplicados y fill-forward incorrecto. Esto protegerá específicamente el
   seam introducido por `CreateUnderlyingDataEnumerator()`.

9. **No existe todavía un benchmark apples-to-apples entre BinaryDataLoader y el streaming actual**

   La nueva arquitectura elimina estructuralmente la retención `O(N)` del loader
   y la sustituye por consumo incremental con memoria acotada. Los tests actuales
   muestran buenas métricas de memoria y throughput, pero no existe una corrida
   idéntica comparando directamente el commit anterior contra el actual.

   Antes de documentar afirmaciones como `5x`, `7x` o similares, debe realizarse
   un benchmark controlado usando exactamente dataset, rango, hardware,
   configuración y workload iguales sobre ambos commits. Deben compararse al
   menos tiempo total, time-to-first-bar, peak managed heap, working set/private
   bytes y GC.

10. **La documentación temporal debe separarse del producto final**

    El commit temporal contiene `CODEX_BINARY_PIPELINE_MIGRATION.md` y
    `CODEX-MD-Chat-Action results.md`. El segundo describe estados históricos,
    SHAs, divergencias y resultados propios de una sesión concreta, por lo que no
    debería terminar mezclado como documentación permanente del Engine.

    Antes del squash final debe decidirse qué documentación se conserva. La
    arquitectura estable debería quedar en un documento técnico limpio, mientras
    los informes de Codex y specs iterativos se archivan como historial de
    ingeniería o se mantienen fuera del commit funcional final.

11. **La rama debe volver a validarse contra el upstream actual antes del cierre**

    La rama Auroboros fue correctamente rebasada sobre el `master` actualizado que
    existía durante la migración, pero QuantConnect continúa desarrollando LEAN y
    upstream ya ha avanzado nuevamente.

    Esto es normal y no implica un fallo del pipeline. Antes del squash/release
    definitivo conviene hacer una última sincronización controlada con upstream,
    resolver cualquier conflicto en los dos extension seams introducidos y volver
    a ejecutar la suite crítica. No debe hacerse un rebase innecesariamente en
    cada micro-iteración; basta con hacerlo en puntos de estabilización.

12. **Formalizar qué partes pertenecen al contrato soportado y cuáles son no-goals**

    La factory actualmente soporta explícitamente Forex con `TradeBar` y
    `QuoteBar`. Tick, Equity y otros SecurityTypes son rechazados.

    Esto es razonable para el alcance actual, pero debe quedar documentado como una
    decisión explícita del producto y no como una limitación accidental. La
    siguiente iteración debe separar claramente `supported`, `unsupported` y
    `future extension`, evitando que futuros cambios interpreten estas
    restricciones como bugs.

## Criterio general de cierre de esta iteración

La siguiente iteración debe resolver primero los problemas de CORRECCIÓN y
CONTRATO antes que las optimizaciones puras.

El orden conceptual recomendado es:

correctness/semantics
→ ABI
→ tests de lifecycle/timezone
→ política de timeframe
→ overlap behavior
→ benchmarking
→ optimizaciones de seek

No debe iniciarse todavía una arquitectura de shared-memory, IPC o cache de
objetos entre procesos.

El objetivo actual sigue siendo que cada proceso LEAN pueda leer los mismos
archivos binarios de manera correcta, streaming, determinista y con memoria
acotada.

El problema de orquestación multi-process pertenece a una fase posterior de
Auroboros.