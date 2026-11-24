@{
    Severity = @('Error', 'Warning')
    IncludeRules = @(
        'PSUseDeclaredVarsMoreThanAssignments',
        'PSAvoidUsingEmptyCatchBlock',
        'PSAvoidAssignmentToAutomaticVariable',
        'PSPossibleIncorrectComparisonWithNull',
        'PSAvoidUsingPositionalParameters'
    )
    Rules = @{
        PSAvoidAssignmentToAutomaticVariable = @{
            Enable = $true
        }
        PSAvoidUsingEmptyCatchBlock = @{
            Enable = $true
        }
        PSUseDeclaredVarsMoreThanAssignments = @{
            Enable = $true
        }
        PSPossibleIncorrectComparisonWithNull = @{
            Enable = $true
        }
    }
}
