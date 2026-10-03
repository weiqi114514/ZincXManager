#pragma once
#include <string_view>

#include "log_level.h"

namespace logging
{
    class ILogger
    {
        public:
        virtual ~ILogger() = default;
        virtual void log(LogLevel level, std::string_view message) = 0;
    };


    class Logger : public ILogger
    {
        public:
        void log(LogLevel level, std::string_view message) override;
    };
}
