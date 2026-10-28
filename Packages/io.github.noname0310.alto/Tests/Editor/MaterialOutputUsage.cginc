float4 _HiddenUniform;
#define AOO_PROPERTY(prefix) prefix##Only
float4 AOO_PROPERTY(_Macro);
#if defined(AOO_ALTERNATE_VARIANT)
float4 _AlternateUniform;
#endif
